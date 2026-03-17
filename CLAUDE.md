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

### Other
- `POST /api/auth/register`, `POST /api/auth/login`
- `POST /api/chat` (multipart, supports image upload)
- `GET/PUT /api/profile`, `PUT /api/profile/timezone`
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
- Docker Compose: PostgreSQL + API + Caddy reverse proxy
- GitHub Actions: push to main triggers SSH deploy to EC2
- Domain: api.useorbit.org
