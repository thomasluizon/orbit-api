# External Integrations

**Analysis Date:** 2026-02-07

## APIs & External Services

**AI Providers (Configurable):**
- Google Gemini API - AI-driven chat intent processing (default configured)
  - SDK/Client: HttpClient with custom serialization
  - Auth: API key via `GeminiSettings.ApiKey`
  - Configuration: `src/Orbit.Infrastructure/Configuration/GeminiSettings.cs`
  - Implementation: `src/Orbit.Infrastructure/Services/GeminiIntentService.cs`
  - Model: `gemini-2.5-flash` (configurable)
  - Endpoint: `https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`
  - Features: Exponential backoff retry logic for rate limiting, temperature=0.1 for consistent JSON output, response MIME type forced to application/json
  - Performance: ~1.6 seconds per request

- Ollama Local LLM - Fallback/alternative AI provider (requires local server)
  - SDK/Client: HttpClient
  - Auth: None required (local)
  - Configuration: `src/Orbit.Infrastructure/Configuration/OllamaSettings.cs`
  - Implementation: `src/Orbit.Infrastructure/Services/AiIntentService.cs` (note: file name is `AiIntentService.cs` but contains `OllamaIntentService` class)
  - Model: `phi3.5:3.8b` (configurable, can be any Ollama model)
  - Endpoint: `http://localhost:11434/api/chat`
  - Features: Markdown code block stripping (```json wrapper removal), JSON format enforcement, streaming disabled
  - Performance: ~30 seconds per request
  - Requirements: `ollama serve` must be running locally before API starts if using Ollama

**Provider Selection:**
- Controlled via `AiProvider` configuration in `appsettings.json`
  - "Gemini" (case-insensitive) routes to Gemini API
  - "Ollama" (default) routes to local Ollama server
  - Logic in `src/Orbit.Api/Program.cs` lines 56-78

## Data Storage

**Databases:**
- PostgreSQL 12+ (primary)
  - Connection string: `appsettings.json` - `ConnectionStrings.DefaultConnection`
  - Format: Host=localhost;Port=5432;Database=orbit_db;Username=...;Password=...
  - Client: Npgsql 10.0.1 (ADO.NET provider)
  - ORM: Entity Framework Core 10.0.2 with Npgsql.EntityFrameworkCore.PostgreSQL
  - DbContext: `src/Orbit.Infrastructure/Persistence/OrbitDbContext.cs`
  - Initialization: EnsureCreatedAsync() for MVP (no migrations)
  - Tables:
    - `User` - User accounts with unique email index
    - `Habit` - User habits with isActive index on (UserId, IsActive)
    - `HabitLog` - Habit completion logs with composite index on (HabitId, Date)
    - `TaskItem` - User tasks with composite index on (UserId, Status)
  - Relationships: Habits → HabitLogs (cascade delete), Users → Habits (cascade), Users → Tasks (cascade)
  - Special Types:
    - `Days` property on Habit stored as PostgreSQL text[] array (System.DayOfWeek[])
    - FrequencyUnit and FrequencyQuantity enums for flexible scheduling

**File Storage:**
- Not used - Application is stateless for file handling

**Caching:**
- Not implemented - Queries hit database directly

## Authentication & Identity

**Auth Provider:**
- Custom JWT implementation
  - Provider: Self-hosted JWT using System.IdentityModel.Tokens.Jwt
  - Implementation: `src/Orbit.Infrastructure/Services/JwtTokenService.cs`
  - Configuration: `src/Orbit.Infrastructure/Configuration/JwtSettings.cs`
  - Token Claims: UserId in standard JWT claims
  - Signature: HMAC-SHA256 with UTF-8 encoded secret key
  - Validation: Issuer, Audience, Lifetime, IssuerSigningKey
  - ClockSkew: Zero (strict time validation)
  - Expiry: Configurable hours (default 24) via `Jwt.ExpiryHours`

**Password Hashing:**
- BCrypt.Net-Next 4.0.3
  - Implementation: `src/Orbit.Infrastructure/Services/PasswordHasher.cs`
  - Per-user salt generation

**Auth Endpoints:**
- `POST /api/auth/register` - Create new user (AuthController.cs)
- `POST /api/auth/login` - Authenticate and receive JWT token (AuthController.cs)
- Subsequent requests include: `Authorization: Bearer {token}` header

## Monitoring & Observability

**Error Tracking:**
- Not configured - Application uses standard .NET logging

**Logs:**
- Built-in ILogger<T> via ASP.NET Core DI
- Log levels: Information (default), Warning, Error
- Configuration: `appsettings.json` - Logging section
- Notable logging in AI services:
  - GeminiIntentService: Performance timings, API calls, deserialization steps
  - OllamaIntentService: Performance timings, markdown stripping, API calls
  - SystemPromptBuilder: No explicit logging (static method)
- Log output: Console/Debug window (development)

## CI/CD & Deployment

**Hosting:**
- Not configured - Application ready for cloud deployment (any ASP.NET Core host)
- Default: Local development on localhost:5000 (http) or localhost:5001 (https)

**CI Pipeline:**
- Not configured - Repository has no CI/CD workflows

**Docker:**
- Not detected - No Dockerfile or docker-compose files

## Environment Configuration

**Required env vars (for appsettings.Development.json):**
- `Gemini:ApiKey` - Google Gemini API key (obtain from Google Cloud Console)
- `Jwt:SecretKey` - Secure secret for JWT token signing (minimum 32 characters recommended)
- `ConnectionStrings:DefaultConnection` - PostgreSQL connection string with username/password

**Optional env vars:**
- `AiProvider` - Set to "Gemini" or "Ollama" (default is "Ollama")
- `Ollama:BaseUrl` - Ollama server URL (default "http://localhost:11434")
- `Ollama:Model` - Ollama model name (default "phi3.5:3.8b")
- `Gemini:Model` - Gemini model name (default "gemini-2.5-flash")
- `Jwt:ExpiryHours` - JWT token expiry in hours (default 24)

**Secrets location:**
- `.env` file is gitignored - Development secrets go in `appsettings.Development.json`
- Production: Use environment variables or secure secret manager (Azure Key Vault, etc.)

## Webhooks & Callbacks

**Incoming:**
- None detected

**Outgoing:**
- None detected

## API Integration Points

**AI Intent Processing:**
- Used by: `src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs`
- Input: User message string + active habits + pending tasks
- Output: AiActionPlan with Actions (CreateHabit, LogHabit, CreateTask, CompleteTask, Reject)
- Error handling: Result<T> pattern with detailed error messages
- Rate limiting: Gemini has exponential backoff (2s, 4s, 8s delays on 429 responses)

**Database Operations:**
- Repository pattern: `src/Orbit.Infrastructure/Persistence/GenericRepository.cs`
- Async/await throughout: All DB operations are cancellation-token aware
- Change tracking: EF Core automatic tracking in create/update scenarios, manual handling in delete

---

*Integration audit: 2026-02-07*
