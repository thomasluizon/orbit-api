# Architecture

**Analysis Date:** 2026-02-07

## Pattern Overview

**Overall:** Clean Architecture with CQRS via MediatR

**Key Characteristics:**
- Strict separation between Domain, Application, Infrastructure, and API layers
- Command Query Responsibility Segregation (CQRS) for all application operations via MediatR
- Repository + Unit of Work pattern for data access
- Result pattern for error handling throughout the system
- Multi-provider AI architecture supporting pluggable LLM services (Gemini and Ollama)
- Dependency injection orchestrated in `Orbit.Api/Program.cs`

## Layers

**Domain Layer:**
- Purpose: Core business logic, entities, enums, and interfaces defining contracts
- Location: `src/Orbit.Domain/`
- Contains:
  - Entities (`User`, `Habit`, `HabitLog`, `TaskItem`) with factory methods and domain behaviors
  - Enums (`FrequencyUnit`, `HabitType`, `TaskItemStatus`, `AiActionType`)
  - Common abstractions (`Entity` base class, `Result` pattern)
  - Interface contracts (`IGenericRepository`, `IAiIntentService`, `IUnitOfWork`, `ITokenService`, `IPasswordHasher`)
  - Domain models (`AiActionPlan`, `AiAction`) representing AI intent results
- Depends on: Nothing (no external dependencies)
- Used by: All other layers

**Application Layer:**
- Purpose: Business logic orchestration via CQRS commands/queries, handlers, and domain service coordination
- Location: `src/Orbit.Application/`
- Contains:
  - Commands: `RegisterCommand`, `CreateHabitCommand`, `LogHabitCommand`, `DeleteHabitCommand`, `CreateTaskCommand`, `UpdateTaskCommand`, `DeleteTaskCommand`, `ProcessUserChatCommand`
  - Queries: `LoginQuery`, `GetHabitsQuery`, `GetTasksQuery`
  - MediatR handlers for each command/query with business logic
  - No direct database access (uses repositories)
  - Orchestrates domain entities and AI intent services
- Depends on: Domain (entities, enums, interfaces, result pattern)
- Used by: API layer

**Infrastructure Layer:**
- Purpose: Technical implementation of domain interfaces and external integrations
- Location: `src/Orbit.Infrastructure/`
- Contains:
  - **Persistence**: `OrbitDbContext` (EF Core DbContext), `GenericRepository<T>` (CRUD operations), `UnitOfWork` (transaction coordination)
  - **Services**:
    - `GeminiIntentService` and `OllamaIntentService` (IAiIntentService implementations)
    - `SystemPromptBuilder` (shared prompt engineering)
    - `JwtTokenService` (ITokenService implementation)
    - `PasswordHasher` (IPasswordHasher implementation)
  - **Configuration**: `GeminiSettings`, `OllamaSettings`, `JwtSettings` (options pattern binding)
  - Database connection via Npgsql 10.0.0 with PostgreSQL
  - HttpClient-based AI provider calls
- Depends on: Domain interfaces only (not concrete domain logic)
- Used by: API layer and Application handlers via dependency injection

**API Layer:**
- Purpose: HTTP REST endpoint contracts, request routing, authentication enforcement
- Location: `src/Orbit.Api/`
- Contains:
  - Controllers: `AuthController`, `ChatController`, `HabitsController`, `TasksController`
  - Extension methods: `HttpContextExtensions` (JWT user ID extraction)
  - `Program.cs` (dependency injection setup, authentication/authorization configuration, database initialization)
  - Swagger/OpenAPI integration via built-in ASP.NET Core tooling
  - Authentication middleware (JWT Bearer scheme)
- Depends on: Application (MediatR commands/queries), Domain (entities, enums)
- Used by: HTTP clients

## Data Flow

**Standard Command Flow (e.g., Create Habit):**

1. HTTP POST to `HabitsController.CreateHabit()` with JWT token in Authorization header
2. Controller extracts userId via `HttpContext.GetUserId()` from JWT claims
3. Controller creates `CreateHabitCommand` and sends via `IMediator.Send()`
4. MediatR routes to `CreateHabitCommandHandler` in Application layer
5. Handler calls `Habit.Create()` (domain factory) with validation
6. If valid, handler calls `habitRepository.AddAsync()` to stage entity
7. Handler calls `unitOfWork.SaveChangesAsync()` to persist to PostgreSQL
8. Handler returns `Result<Guid>` with new habit ID
9. Controller returns `CreatedAtAction()` with 201 status

**AI Chat Flow:**

1. HTTP POST to `ChatController.ProcessChat()` with message and JWT token
2. Controller sends `ProcessUserChatCommand` with userId and message text
3. `ProcessUserChatCommandHandler` executes:
   a. Database queries: Fetch active habits and pending tasks for context (uses `habitRepository.FindAsync()`)
   b. AI intent service call: Send message + context to Gemini or Ollama via `IAiIntentService.InterpretAsync()`
   c. Response parsing: Deserialize `AiActionPlan` with list of `AiAction` objects
   d. Action execution: For each action, dispatch appropriate handler (LogHabit, CreateHabit, CreateTask, UpdateTask)
   e. Persistence: Single `unitOfWork.SaveChangesAsync()` batches all changes
   f. Return executed action summaries and AI message response
4. Performance logged at each step with elapsed milliseconds

**State Management:**

- Entity change tracking via EF Core DbContext
- `GenericRepository.Update()` marks entities as modified in change tracker
- Repositories do NOT call SaveChanges (handler/command responsible)
- Single `UnitOfWork.SaveChangesAsync()` per command handles transaction boundaries
- Result pattern ensures all operations return explicit success/failure state

## Key Abstractions

**Result Pattern:**
- Purpose: Functional error handling without exceptions for business operations
- Examples: `src/Orbit.Domain/Common/Result.cs`
- Pattern: Generic `Result<T>` with `IsSuccess`, `Error`, and `Value` properties
- Usage: All command/query handlers return `Result<T>` or `Result`
- Enforces explicit success/failure branches in callers

**Entity Base Class:**
- Purpose: Standardized identity and equality semantics
- Examples: `src/Orbit.Domain/Entities/User.cs`, `Habit.cs`, `TaskItem.cs`, `HabitLog.cs`
- Pattern: GUID-based identity with `{ get; init; }` for immutability, default `Guid.NewGuid()`
- Factory methods on entities (e.g., `Habit.Create()`) return `Result<Habit>` for validation
- Domain methods (e.g., `habit.Log()`, `task.MarkCompleted()`) validate state and return `Result`

**Repository Pattern:**
- Purpose: Abstract data access behind contracts
- Examples: `src/Orbit.Infrastructure/Persistence/GenericRepository.cs`
- Interface: `IGenericRepository<T>` (FindAsync, GetByIdAsync, GetAllAsync, AddAsync, Update, Remove)
- Implementation: Generic `GenericRepository<T>` using EF Core DbSet operations
- Key: Repositories do NOT call SaveChanges; handlers coordinate persistence

**Unit of Work Pattern:**
- Purpose: Coordinate transaction boundaries across multiple repositories
- Examples: `src/Orbit.Infrastructure/Persistence/UnitOfWork.cs`
- Interface: `IUnitOfWork.SaveChangesAsync()`
- Usage: Single call per command/query handler batches all entity changes

**Pluggable AI Intent Service:**
- Purpose: Support multiple LLM providers with consistent interface
- Examples: `src/Orbit.Infrastructure/Services/GeminiIntentService.cs`, `OllamaIntentService.cs`
- Interface: `IAiIntentService.InterpretAsync(message, habits, tasks)`
- Selection: Configured via `"AiProvider"` setting in appsettings (Gemini vs Ollama)
- Shared prompt: `SystemPromptBuilder.BuildSystemPrompt()` generates identical context for both providers
- Request/Response: Internally maps to provider-specific HTTP protocols

## Entry Points

**API Entry Point:**
- Location: `src/Orbit.Api/Program.cs`
- Triggers: Application startup (dotnet run)
- Responsibilities:
  - Register DbContext with PostgreSQL connection string
  - Register repositories and UnitOfWork as scoped services
  - Register authentication (JWT Bearer) with token validation
  - Register authorization policies
  - Configure AI provider (Gemini or Ollama) based on settings
  - Register MediatR handlers from Application assembly
  - Configure ASP.NET Core pipeline (middleware order, Swagger)
  - Initialize database via `EnsureCreatedAsync()` (MVP approach, no migrations)

**HTTP Endpoints:**
- `POST /api/auth/register` → `AuthController.Register()` → `RegisterCommand`
- `POST /api/auth/login` → `AuthController.Login()` → `LoginQuery`
- `POST /api/chat` → `ChatController.ProcessChat()` → `ProcessUserChatCommand`
- `GET /api/habits` → `HabitsController.GetHabits()` → `GetHabitsQuery`
- `POST /api/habits` → `HabitsController.CreateHabit()` → `CreateHabitCommand`
- `POST /api/habits/{id}/log` → `HabitsController.LogHabit()` → `LogHabitCommand`
- `DELETE /api/habits/{id}` → `HabitsController.DeleteHabit()` → `DeleteHabitCommand`
- `GET /api/tasks` → `TasksController.GetTasks()` → `GetTasksQuery`
- `POST /api/tasks` → `TasksController.CreateTask()` → `CreateTaskCommand`
- `PUT /api/tasks/{id}/status` → `TasksController.UpdateTask()` → `UpdateTaskCommand`
- `DELETE /api/tasks/{id}` → `TasksController.DeleteTask()` → `DeleteTaskCommand`

## Error Handling

**Strategy:** Result-based functional approach with controlled exception handling in services

**Patterns:**

- **Domain validation**: Entities return `Result<T>` from factories and methods. Controllers/handlers check `IsFailure` and propagate error message.
- **Database errors**: EF Core exceptions caught in handlers, converted to `Result.Failure()` with context
- **AI service errors**: Gemini/Ollama HTTP failures caught and converted to `Result.Failure()` with error details
- **Authorization errors**: Missing/invalid JWT token raises `UnauthorizedAccessException` in `HttpContext.GetUserId()`
- **Unexpected errors**: Generic exception handlers in command handlers log and return `Result.Failure()`

**Example from Chat Handler:**
```csharp
if (planResult.IsFailure)
    return Result.Failure<ChatResponse>(planResult.Error);
```

## Cross-Cutting Concerns

**Logging:**
- Framework: Microsoft.Extensions.Logging (built-in to ASP.NET Core)
- Approach: Dependency-injected `ILogger<T>` in handlers and services
- Pattern: Structured logging with emoji markers for readability (🚀, 🔵, ✅, ❌)
- Details: Performance timings via `Stopwatch` logged at each step (DB, AI, actions, save)
- Example: `ProcessUserChatCommandHandler` logs elapsed milliseconds for database queries, AI service, action execution, and persistence

**Validation:**
- Domain level: Factory methods and domain methods validate and return `Result`
- Application level: Handlers check factory/method results and propagate errors
- API level: Controllers accept strongly-typed requests, delegate to handlers
- Example: `Habit.Create()` validates userId, title, frequency, habit type, and days constraints

**Authentication:**
- Framework: ASP.NET Core JWT Bearer with IdentityModel tokens
- Configuration: `JwtSettings` (Issuer, Audience, SecretKey, ExpirationMinutes)
- Token generation: `JwtTokenService` creates tokens in login/register
- Token validation: `JwtBearerDefaults` configured in middleware
- User extraction: `HttpContext.GetUserId()` extracts `ClaimTypes.NameIdentifier` (Guid)
- Enforcement: `[Authorize]` attributes on all controllers except Auth

**AI Intent Interpretation:**
- Shared prompt: `SystemPromptBuilder.BuildSystemPrompt()` embeds active habits and pending tasks
- Provider abstraction: Both Gemini and Ollama get identical system prompt and user message
- Response format: Requested JSON with structured `AiAction` objects
- Deserialization: Case-insensitive with enum string converter for PascalCase enum names
- Retry logic: Gemini service has exponential backoff for rate limiting (2s, 4s, 8s)

---

*Architecture analysis: 2026-02-07*
