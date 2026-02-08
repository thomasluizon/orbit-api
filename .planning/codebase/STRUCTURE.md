# Codebase Structure

**Analysis Date:** 2026-02-07

## Directory Layout

```
Orbit/
├── .planning/
│   └── codebase/               # Analysis documentation (ARCHITECTURE.md, STRUCTURE.md, etc.)
├── src/
│   ├── Orbit.Domain/           # Core business logic and contracts
│   ├── Orbit.Application/       # Use cases and command/query handlers
│   ├── Orbit.Infrastructure/    # Data access, external services, configuration
│   └── Orbit.Api/              # HTTP endpoints and ASP.NET Core setup
├── tests/
│   └── Orbit.IntegrationTests/  # Integration tests for chat, habits, tasks
├── Orbit.slnx                  # Modern solution file
└── README.md                    # Project overview
```

## Directory Purposes

**Orbit.Domain:**
- Purpose: Entity definitions, business logic, domain interfaces, and error handling abstractions
- Contains:
  - `Common/` - Entity base class, Result pattern
  - `Entities/` - User, Habit, HabitLog, TaskItem with factory methods and domain behaviors
  - `Enums/` - FrequencyUnit, HabitType, TaskItemStatus, AiActionType
  - `Interfaces/` - Service contracts (IGenericRepository, IAiIntentService, IUnitOfWork, ITokenService, IPasswordHasher)
  - `Models/` - Domain models (AiAction, AiActionPlan) for AI responses
- Key files:
  - `Entity.cs` - GUID-based identity with immutable init-only Id
  - `Result.cs` - Functional error handling with success/failure semantics
- No dependencies on other projects

**Orbit.Application:**
- Purpose: CQRS command/query implementations orchestrating domain logic and repositories
- Contains:
  - `Auth/` - Commands (RegisterCommand), Queries (LoginQuery) for user authentication
  - `Chat/` - ProcessUserChatCommand handler with AI intent execution
  - `Habits/` - Commands (Create, Log, Delete), Queries (GetHabits)
  - `Tasks/` - Commands (Create, Update, Delete), Queries (GetTasks)
- Key files:
  - `Chat/Commands/ProcessUserChatCommand.cs` - Central AI coordination handler with performance logging
  - Habit/Task commands follow standard CQRS pattern with factory validation
- Depends on: Domain (entities, enums, interfaces, result pattern)

**Orbit.Infrastructure:**
- Purpose: Technical implementations of domain interfaces and external integrations
- Contains:
  - `Persistence/` - EF Core DbContext, GenericRepository, UnitOfWork, database configuration
  - `Services/` - AI intent services (Gemini, Ollama), prompt builder, JWT token service, password hasher
  - `Configuration/` - Settings classes for GeminiSettings, OllamaSettings, JwtSettings
- Key files:
  - `Persistence/OrbitDbContext.cs` - EF Core DbContext with entity configurations, indexes, conversions
  - `Services/GeminiIntentService.cs` - Google Gemini API client with retry logic
  - `Services/OllamaIntentService.cs` - Local Ollama LLM client
  - `Services/SystemPromptBuilder.cs` - Shared prompt engineering for both providers
  - `Services/JwtTokenService.cs` - JWT token generation and validation
  - `Services/PasswordHasher.cs` - BCrypt password hashing
- Depends on: Domain interfaces only
- Database: PostgreSQL via Npgsql 10.0.0

**Orbit.Api:**
- Purpose: HTTP REST API surface with ASP.NET Core middleware and dependency injection
- Contains:
  - `Controllers/` - AuthController, ChatController, HabitsController, TasksController (all [Authorize])
  - `Extensions/` - HttpContextExtensions for JWT user extraction
  - `Properties/` - launchSettings.json for local development
  - `Program.cs` - DI setup, authentication config, AI provider selection, database initialization
- Key files:
  - `Program.cs` - Central configuration (DbContext, repositories, JWT, AI provider, MediatR, Swagger)
  - Controllers expose MediatR commands/queries as HTTP endpoints
- Depends on: Application (MediatR handlers), Domain (entities, enums)

**Orbit.IntegrationTests:**
- Purpose: End-to-end testing of chat processing, habit/task operations, AI intent handling
- Contains: Test cases for task creation, habit creation/logging, AI action validation
- Key patterns:
  - Creates unique user per run (GUID-based email) via register/login
  - Uses DELETE endpoints for cleanup (no database recreation)
  - Tests with both Gemini (faster, more reliable) and Ollama (slower, variable JSON)
- Depends on: All layers via HTTP client

## Key File Locations

**Entry Points:**
- `src/Orbit.Api/Program.cs` - Application bootstrap, DI registration, middleware pipeline
- `src/Orbit.Api/Controllers/AuthController.cs` - User registration and login endpoints
- `src/Orbit.Api/Controllers/ChatController.cs` - AI chat message processing endpoint

**Core Domain Logic:**
- `src/Orbit.Domain/Entities/User.cs` - User registration with email/password validation
- `src/Orbit.Domain/Entities/Habit.cs` - Habit creation with frequency/days, logging with validation
- `src/Orbit.Domain/Entities/TaskItem.cs` - Task creation and status transitions
- `src/Orbit.Domain/Common/Result.cs` - Error handling abstraction

**Application Handlers:**
- `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs` - Main AI coordination (100+ lines with logging)
- `src/Orbit.Application/Auth/Commands/RegisterCommand.cs` - User registration handler
- `src/Orbit.Application/Auth/Queries/LoginQuery.cs` - User login with token generation
- `src/Orbit.Application/Habits/Commands/CreateHabitCommand.cs` - Habit creation handler
- `src/Orbit.Application/Habits/Queries/GetHabitsQuery.cs` - Active habits retrieval
- `src/Orbit.Application/Tasks/Commands/CreateTaskCommand.cs` - Task creation handler

**Infrastructure & Data:**
- `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs` - EF Core DbContext (52 lines with model config)
- `src/Orbit.Infrastructure/Persistence/GenericRepository.cs` - Repository implementation with Find, GetById, Add, Update, Remove
- `src/Orbit.Infrastructure/Services/GeminiIntentService.cs` - Gemini API client with rate limit retry (195 lines)
- `src/Orbit.Infrastructure/Services/OllamaIntentService.cs` - Ollama API client (similar structure)
- `src/Orbit.Infrastructure/Services/SystemPromptBuilder.cs` - Shared AI prompt generation

**Configuration:**
- `src/Orbit.Api/appsettings.json` - Default settings (AiProvider, database, JWT, API URLs)
- `src/Orbit.Api/appsettings.Development.json` - (gitignored) Local dev secrets (Gemini API key, DB password)
- `src/Orbit.Infrastructure/Configuration/JwtSettings.cs` - JWT configuration binding
- `src/Orbit.Infrastructure/Configuration/GeminiSettings.cs` - Gemini API endpoint and model
- `src/Orbit.Infrastructure/Configuration/OllamaSettings.cs` - Ollama base URL

## Naming Conventions

**Files:**
- Entity files: Singular noun (`User.cs`, `Habit.cs`, `TaskItem.cs`)
- Handler files: Command/Query name + "Handler" (`CreateHabitCommandHandler`, `GetHabitsQueryHandler`)
- Interface files: "I" prefix (`IGenericRepository.cs`, `IAiIntentService.cs`)
- Service files: Service name + "Service" (`GeminiIntentService.cs`, `JwtTokenService.cs`)

**Directories:**
- Feature areas: Plural noun (`Auth`, `Habits`, `Tasks`, `Chat`)
- Pattern types: `Commands/`, `Queries/`, `Services/`, `Persistence/`
- Infrastructure concerns: `Configuration/`, `Services/`, `Persistence/`

**Classes & Types:**
- Entities: PascalCase noun (`User`, `Habit`, `TaskItem`)
- Services: PascalCase + "Service" (`GeminiIntentService`, `JwtTokenService`)
- Commands: PascalCase + "Command" (`CreateHabitCommand`, `RegisterCommand`)
- Queries: PascalCase + "Query" (`GetHabitsQuery`, `LoginQuery`)
- Handlers: Command/Query name + "Handler" (`CreateHabitCommandHandler`)
- DTOs/Requests: Inside controllers as `public record` or nested class
- Enums: PascalCase + "Unit/Type/Status" (`FrequencyUnit`, `HabitType`, `TaskItemStatus`)

**Properties & Methods:**
- Properties: PascalCase (`UserId`, `IsActive`, `CreatedAtUtc`)
- Methods: PascalCase verb-noun (`Create()`, `Log()`, `MarkCompleted()`)
- Private fields: camelCase with underscore prefix (`_logs`, `_settings`)
- Parameters: camelCase (`userId`, `habitId`, `cancellationToken`)

**Constants & Settings:**
- Configuration section names: PascalCase + "SectionName" (e.g., `JwtSettings.SectionName`)
- API routes: lowercase with hyphens where needed (`api/auth`, `api/chat`, `api/habits`, `api/tasks`)

## Where to Add New Code

**New Feature (e.g., Goals management):**
- Primary code:
  - Domain entities: `src/Orbit.Domain/Entities/Goal.cs`
  - Domain enums: `src/Orbit.Domain/Enums/GoalStatus.cs` (if needed)
  - Application commands: `src/Orbit.Application/Goals/Commands/CreateGoalCommand.cs`
  - Application queries: `src/Orbit.Application/Goals/Queries/GetGoalsQuery.cs`
  - Infrastructure repository: Handled by generic `IGenericRepository<Goal>`
- Tests: `tests/Orbit.IntegrationTests/GoalsTests.cs`
- API endpoint: Add handler method to new `GoalsController.cs` in `src/Orbit.Api/Controllers/`

**New Command Handler:**
- Pattern: Follow `CreateHabitCommandHandler` structure
- Location: `src/Orbit.Application/{Feature}/Commands/{CommandName}Handler.cs`
- Implementation:
  1. Inject `IGenericRepository<T>` and `IUnitOfWork`
  2. Validate command parameters
  3. Call entity factory method (`Entity.Create()`)
  4. If factory returns failure, propagate error
  5. Add/update repository
  6. Call `unitOfWork.SaveChangesAsync()`
  7. Return `Result<T>` with value or error

**New Query Handler:**
- Pattern: Follow `GetHabitsQueryHandler` structure
- Location: `src/Orbit.Application/{Feature}/Queries/{QueryName}Handler.cs`
- Implementation:
  1. Inject `IGenericRepository<T>`
  2. Call repository method (`FindAsync()`, `GetAllAsync()`, etc.)
  3. Return results directly (no error wrapping for reads)
  4. No `SaveChangesAsync()` call

**New Endpoint:**
- Add to existing controller or create new controller in `src/Orbit.Api/Controllers/`
- Decorate controller with `[Authorize]` (unless auth-exempt like register/login)
- Follow pattern:
  1. Extract userId via `HttpContext.GetUserId()`
  2. Create command/query object
  3. Send via `mediator.Send()`
  4. Return appropriate HTTP status (`Ok()`, `BadRequest()`, `CreatedAtAction()`, `NoContent()`)

**Utilities and Shared Helpers:**
- Shared helpers: `src/Orbit.Infrastructure/Services/` (e.g., `SystemPromptBuilder`)
- API extensions: `src/Orbit.Api/Extensions/` (e.g., `HttpContextExtensions`)
- Domain abstractions: `src/Orbit.Domain/Common/` (e.g., `Result`, `Entity`)

## Special Directories

**bin/ and obj/:**
- Purpose: Build artifacts and compiled output
- Generated: Yes
- Committed: No (in .gitignore)

**.planning/codebase/:**
- Purpose: Architecture and codebase analysis documents (ARCHITECTURE.md, STRUCTURE.md, CONVENTIONS.md, TESTING.md, CONCERNS.md)
- Generated: No (hand-written analysis)
- Committed: Yes

**.idea/:**
- Purpose: JetBrains IDE configuration (ReSharper for Visual Studio)
- Generated: Yes
- Committed: Yes (shared team configuration)

**appsettings.Development.json:**
- Purpose: Local secrets and sensitive configuration
- Generated: No
- Committed: No (in .gitignore)
- Contains: Gemini API key, database password, JWT secret key

---

*Structure analysis: 2026-02-07*
