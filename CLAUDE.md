# Orbit API

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
- Firebase Admin SDK 3.5.0 (FCM push notifications)
- Lib.Net.Http.WebPush 3.3.1 (VAPID web push)

## Architecture

```
src/
  Orbit.Api/           - Controllers, middleware, DI config, Program.cs
    Extensions/        - ResultActionResultExtensions (PayGate-aware IActionResult)
    Middleware/         - SecurityHeadersMiddleware, ValidationExceptionHandler
  Orbit.Application/   - Commands, Queries, Validators, DTOs (CQRS)
    Common/            - PaginatedResponse<T>, ErrorMessages, AppConstants,
                         CacheInvalidationHelper, ResultExtensions, PayGateService
    Habits/Services/   - HabitScheduleService (schedule calculation logic)
    Habits/Validators/ - SharedHabitRules (shared Create/Update validation)
  Orbit.Domain/        - Entities, Enums, Interfaces, Value Objects
  Orbit.Infrastructure/- DbContext, Repositories, AI services, JWT, Migrations
tests/
  Orbit.IntegrationTests/ - xUnit + FluentAssertions, real DB, sequential
```

## Key Commands

```bash
dotnet run --project src/Orbit.Api                          # Run API
dotnet test tests/Orbit.IntegrationTests                    # Run tests
dotnet ef migrations add <Name> --project src/Orbit.Infrastructure --startup-project src/Orbit.Api
dotnet ef database update --project src/Orbit.Infrastructure --startup-project src/Orbit.Api
docker compose up -d --build                                # Docker deployment
```

## API Endpoints

### Habits (paginated, schedule-aware)
- `GET /api/habits?dateFrom=&dateTo=&includeOverdue=&search=&frequencyUnit=&isCompleted=&page=&pageSize=` - Paginated list with `scheduledDates[]` and `isOverdue`. `dateFrom`/`dateTo` required. Returns `PaginatedResponse<HabitScheduleItem>`.
- `GET /api/habits/{id}` - Single habit detail with original `DueDate`
- `POST /api/habits` - Create habit
- `PUT /api/habits/{id}` - Update habit
- `DELETE /api/habits/{id}` - Delete habit
- `POST /api/habits/{id}/log` - Toggle log for today
- `GET /api/habits/{id}/logs` - Log history
- `GET /api/habits/{id}/metrics` - Streaks, completion rates
- `POST /api/habits/bulk` - Bulk create
- `DELETE /api/habits/bulk` - Bulk delete
- `PUT /api/habits/reorder` - Reorder positions
- `PUT /api/habits/{id}/parent` - Move to new parent
- `POST /api/habits/{parentId}/sub-habits` - Create sub-habit
- `POST /api/habits/{id}/duplicate` - Duplicate a habit
- `GET /api/habits/summary?dateFrom=&dateTo=&includeOverdue=&language=` - AI daily summary (cached)

### Notifications & Push
- `GET /api/notification` - List (last 50 + unread count)
- `PUT /api/notification/{id}/read` - Mark as read
- `PUT /api/notification/read-all` - Mark all as read
- `DELETE /api/notification/{id}` - Delete
- `DELETE /api/notification/all` - Delete all
- `POST /api/notification/subscribe` - Register push subscription
- `POST /api/notification/unsubscribe` - Remove subscription
- `POST /api/notification/test-push` - Send test notification

### Auth
- `POST /api/auth/send-code` - Send verification code
- `POST /api/auth/verify-code` - Verify code and login
- `POST /api/auth/google` - Google OAuth

### Other
- `POST /api/chat` (multipart, supports image upload)
- `GET/PUT /api/profile`, `PUT /api/profile/timezone`
- `PUT /api/profile/onboarding` - Mark onboarding complete
- `PUT /api/profile/ai-memory` - Toggle AI memory
- `PUT /api/profile/ai-summary` - Toggle daily summary
- `GET/POST /api/user-facts`, `PUT/DELETE /api/user-facts/{id}`
- `GET /health`

## Schedule Calculation

`HabitScheduleService` (in `Application/Habits/Services/`) determines if a habit is due on a given date:
- **One-time tasks** (`FrequencyUnit = null`): due only on their `DueDate`
- **Daily** (`Day`, qty=1): every day, filtered by active `Days` if set
- **Every N days** (`Day`, qty>1): modular arithmetic from anchor date
- **Weekly/Monthly/Yearly**: same weekday/day-of-month/date with interval check
- **Overdue detection**: `includeOverdue=true` returns habits where `DueDate < dateFrom && !IsCompleted`

## Deployment

- **Hosting:** Render (Docker, auto-deploy on push to main)
- **Database:** Supabase PostgreSQL (session pooler)
- **Auth:** Supabase Auth for Google OAuth, custom email/code via Resend
- **Domain:** api.useorbit.org
- **Firebase:** Project orbit-11d4a, FCM for native push
- **Env vars:** Configured in Render dashboard
- **Observability:** Render built-in logs + metrics. Production log level: Information (Microsoft.AspNetCore/EF Core: Warning)

## Push Notifications

- **Dual delivery:** `PushNotificationService` routes by subscription type -- `p256dh == "fcm"` sends via Firebase Admin SDK, otherwise via VAPID Web Push.
- **Scheduler:** `ReminderSchedulerService` (BackgroundService) runs every 1 minute, checks habits with `ReminderEnabled && DueTime != null`, sends push + creates in-app notification. `SentReminder` table prevents duplicates.

## Working Principles

### Plan First
- Enter plan mode for any non-trivial task (3+ steps or architectural decisions).
- If something goes sideways, STOP and re-plan immediately.

### Simplicity & Root Causes
- Make every change as simple as possible. Impact minimal code.
- Find root causes. No temporary fixes. Senior developer standards.

### Verification Before Done
- Never mark a task complete without proving it works.
- Ask: "Would a staff engineer approve this?"
- Run tests, check logs, demonstrate correctness.

### Autonomous Bug Fixing
- When given a bug report: just fix it. Don't ask for hand-holding.
- Zero context switching required from the user.

### Subagent Strategy
- Use subagents liberally to keep main context window clean.
- One task per subagent for focused execution.

### Self-Improvement
- After any correction: capture the lesson so the same mistake doesn't repeat.
- Write rules that prevent the pattern, not just fix the instance.

## Git Workflow

Branch protection is enforced on `main`. No direct pushes, no force pushes, no branch deletion.

### Branching Convention

- `feature/xxx` -- new features
- `fix/xxx` -- bugfixes
- `chore/xxx` -- maintenance, config, docs

### Merge Strategy

- **Squash merge only** -- keeps `main` history linear and clean
- Head branches auto-delete after merge

### Rules

- Never push directly to `main` -- always go through a PR
- Never force push to `main`
- Keep PRs focused: one feature or fix per PR
- **Never reuse a branch after its PR is squash-merged.**

## Testing

- Integration tests (xUnit + FluentAssertions) + unit tests. Run: `dotnet test`
- Every new feature must include tests.
- See `.claude/rules/testing.md` for conventions.
