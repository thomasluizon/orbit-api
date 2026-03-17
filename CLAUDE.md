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

## API Endpoints
- `POST /api/auth/register`, `POST /api/auth/login`
- `GET/POST /api/habits`, `PUT/DELETE /api/habits/{id}`
- `POST /api/habits/{id}/log`, `GET /api/habits/{id}/logs`, `GET /api/habits/{id}/metrics`
- `POST /api/habits/bulk`, `DELETE /api/habits/bulk`, `PUT /api/habits/reorder`
- `PUT /api/habits/{id}/parent`, `POST /api/habits/{parentId}/sub-habits`
- `POST /api/chat` (multipart, supports image upload)
- `GET/PUT /api/profile`, `PUT /api/profile/timezone`
- `GET/POST /api/user-facts`, `PUT/DELETE /api/user-facts/{id}`
- `GET /health`

## Deployment
- Docker Compose: PostgreSQL + API + Caddy reverse proxy
- GitHub Actions: push to main triggers SSH deploy to EC2
- Domain: api.useorbit.org
