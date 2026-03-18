# Orbit API

## Project Overview
.NET 10.0 REST API for the Orbit habit & task management app. Clean Architecture with CQRS via MediatR.

## Tech Stack
- .NET 10.0, C# 13
- PostgreSQL via EF Core 10.0.2 (Npgsql)
- MediatR 14.0.0 (CQRS)
- FluentValidation 12.1.1
- JWT Bearer authentication
- AI: Gemini 2.5 Flash (primary), Ollama phi3.5:3.8b (fallback)
- BCrypt for password hashing
- Scalar for API docs (dev only)

## Architecture
```
src/
  Orbit.Api/           - Controllers, middleware, DI config, Program.cs
  Orbit.Application/   - Commands, Queries, Validators, DTOs (CQRS)
    Common/            - Shared models (PaginatedResponse<T>)
    Habits/Services/   - HabitScheduleService (schedule calculation logic)
  Orbit.Domain/        - Entities, Enums, Interfaces, Value Objects
  Orbit.Infrastructure/- DbContext, Repositories, AI services, JWT, Migrations
tests/
  Orbit.IntegrationTests/ - xUnit + FluentAssertions, real DB, sequential
```

## Key Commands
```bash
# Run API
dotnet run --project src/Orbit.Api

# Run tests (sequential, hits real DB + Gemini API)
dotnet test tests/Orbit.IntegrationTests

# Add EF migration
dotnet ef migrations add <Name> --project src/Orbit.Infrastructure --startup-project src/Orbit.Api

# Apply migrations
dotnet ef database update --project src/Orbit.Infrastructure --startup-project src/Orbit.Api

# Drop database (recreate from scratch)
dotnet ef database drop --project src/Orbit.Infrastructure --startup-project src/Orbit.Api --force

# Docker deployment
docker compose up -d --build
```

## Conventions
- CQRS: Commands (write) and Queries (read) in separate folders per feature
- Result<T> pattern for error handling across all handlers
- FluentValidation validators in Validators/ folder per feature
- Domain entities use factory methods (e.g., `Habit.Create()`, `User.Create()`)
- Generic repository + Unit of Work pattern
- Soft deletes for UserFacts
- All endpoints except /health and auth require JWT Bearer token
- Schedule calculations (frequency, days, intervals) live in `HabitScheduleService` -- never on the frontend
- **Timezone rule:** All user-facing dates MUST use `IUserDateService.GetUserTodayAsync(userId)` to get the user's timezone-aware "today". NEVER use `DateOnly.FromDateTime(DateTime.UtcNow)` for user-facing logic. `DateTime.UtcNow` is only acceptable for: `CreatedAtUtc` timestamps in entity factories, and cache key generation. The user sets their timezone in their profile (`User.TimeZone`). If no timezone is set, it falls back to UTC.

## Working Style

### Plan First
- Enter plan mode for ANY non-trivial task (3+ steps or architectural decisions)
- If something goes sideways, STOP and re-plan immediately -- don't keep pushing
- Use plan mode for verification steps, not just building
- Write detailed specs upfront to reduce ambiguity

### Subagent Strategy
- Use subagents liberally to keep main context window clean
- Offload research, exploration, and parallel analysis to subagents
- For complex problems, throw more compute at it via subagents
- One task per subagent for focused execution

### Self-Improvement
- After ANY correction from the user: update `tasks/lessons.md` with the pattern
- Write rules that prevent the same mistake twice
- Review lessons at session start for relevant project

### Verification Before Done
- Never mark a task complete without proving it works
- Diff behavior between main and your changes when relevant
- Ask yourself: "Would a staff engineer approve this?"
- Run tests, check logs, demonstrate correctness

### Demand Elegance (Balanced)
- For non-trivial changes: pause and ask "is there a more elegant way?"
- If a fix feels hacky: rethink and implement the elegant solution
- Skip this for simple, obvious fixes -- don't over-engineer

### Autonomous Bug Fixing
- When given a bug report: just fix it. Don't ask for hand-holding
- Point at logs, errors, failing tests -- then resolve them
- Zero context switching required from the user

## Task Management

1. **Plan First**: Write plan to `tasks/todo.md` with checkable items
2. **Verify Plan**: Check in before starting implementation
3. **Track Progress**: Mark items complete as you go
4. **Explain Changes**: High-level summary at each step
5. **Document Results**: Add review section to `tasks/todo.md`
6. **Capture Lessons**: Update `tasks/lessons.md` after corrections

## Core Principles
- **Simplicity First**: Make every change as simple as possible. Impact minimal code.
- **No Laziness**: Find root causes. No temporary fixes. Senior developer standards.

## API Endpoints

### Habits (paginated, schedule-aware)
- `GET /api/habits?dateFrom=&dateTo=&includeOverdue=&search=&frequencyUnit=&isCompleted=&page=&pageSize=` - Paginated habit list with calculated `scheduledDates[]` and `isOverdue` flag. `dateFrom` and `dateTo` are required. Returns `PaginatedResponse<HabitScheduleItem>`.
- `GET /api/habits/{id}` - Single habit detail with original `DueDate` (anchor date for frequency calculations).
- `POST /api/habits` - Create habit
- `PUT /api/habits/{id}` - Update habit
- `DELETE /api/habits/{id}` - Delete habit
- `POST /api/habits/{id}/log` - Toggle log for today
- `GET /api/habits/{id}/logs` - Habit log history
- `GET /api/habits/{id}/metrics` - Streaks, completion rates
- `POST /api/habits/bulk` - Bulk create
- `DELETE /api/habits/bulk` - Bulk delete
- `PUT /api/habits/reorder` - Reorder positions
- `PUT /api/habits/{id}/parent` - Move to new parent
- `POST /api/habits/{parentId}/sub-habits` - Create sub-habit
- `POST /api/habits/{id}/duplicate` - Duplicate a habit
- `GET /api/habits/summary?dateFrom=&dateTo=&includeOverdue=&language=` - AI-generated daily summary (cached, invalidated on habit changes)

### Other
- `POST /api/auth/register`, `POST /api/auth/login`
- `POST /api/chat` (multipart, supports image upload)
- `GET/PUT /api/profile`, `PUT /api/profile/timezone`
- `PUT /api/profile/onboarding` - Mark onboarding complete (no request body, one-way flag, idempotent). `HasCompletedOnboarding` defaults to `false` in migration so existing users see the wizard.
- `PUT /api/profile/ai-memory` - Toggle AI memory (request body: `{ "enabled": bool }`)
- `PUT /api/profile/ai-summary` - Toggle daily summary (request body: `{ "enabled": bool }`)
- `GET/POST /api/user-facts`, `PUT/DELETE /api/user-facts/{id}`
- `GET /health`

## Schedule Calculation

`HabitScheduleService` (in `Application/Habits/Services/`) determines if a habit is due on a given date. Used by `GetHabitScheduleQuery` for paginated listing and by the calendar month view.

Key logic:
- **One-time tasks** (`FrequencyUnit = null`): due only on their `DueDate`
- **Daily** (`Day`, qty=1): every day, filtered by active `Days` if set
- **Every N days** (`Day`, qty>1): modular arithmetic from anchor date
- **Weekly/Monthly/Yearly**: same weekday/day-of-month/date with interval check
- **Overdue detection**: `includeOverdue=true` returns habits where `DueDate < dateFrom && !IsCompleted`

The frontend (orbit-ui) consumes this via BFF and never computes schedules.

## Deployment
- **Hosting:** Render (Docker, auto-deploy on push to main)
- **Database:** Supabase PostgreSQL (session pooler)
- **Auth:** Supabase Auth for Google OAuth, custom JWT for email/password
- **Domain:** api.useorbit.org
- **Env vars:** Configured in Render dashboard (connection string, JWT, Supabase, Gemini, CORS)
