# Orbit API

.NET 10.0 REST API for the Orbit habit & task management app. Clean Architecture with CQRS via MediatR.

## Tech Stack

- .NET 10.0, C# 13
- PostgreSQL via EF Core 10.0.2 (Npgsql)
- MediatR 14.0.0 (CQRS)
- FluentValidation 12.1.1
- JWT Bearer authentication
- AI: OpenAI GPT-4.1-mini via OpenAI .NET SDK 2.8.0
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

## Coding Conventions

### CQRS & Patterns

- Commands (write) and Queries (read) in separate folders per feature
- Result<T> pattern for error handling across all handlers
- FluentValidation validators in Validators/ folder per feature
- Domain entities use factory methods (e.g., `Habit.Create()`, `User.Create()`)
- Generic repository + Unit of Work pattern
- Soft deletes for UserFacts
- All endpoints except /health and auth require JWT Bearer token
- Schedule calculations (frequency, days, intervals) live in `HabitScheduleService` -- never on the frontend

### Timezone Rule

All user-facing dates MUST use `IUserDateService.GetUserTodayAsync(userId)` to get the user's timezone-aware "today". NEVER use `DateOnly.FromDateTime(DateTime.UtcNow)` for user-facing logic. `DateTime.UtcNow` is only acceptable for: `CreatedAtUtc` timestamps in entity factories, and cache key generation. The user sets their timezone in their profile (`User.TimeZone`). If no timezone is set, it falls back to UTC.

### Validation

- **Every new feature must include validation for all invalid/edge-case scenarios**, both in the domain entity (factory/update methods) and FluentValidation validators.
- Never rely on the frontend alone for validation -- the backend is the source of truth and safety net.
- Examples: date ranges (end must be after start), time ranges (end time must be after start time), numeric bounds, mutually exclusive options, required fields.

### DRY Patterns

- **Error messages:** Use `ErrorMessages.UserNotFound`, `ErrorMessages.HabitNotFound`, etc. from `Common/ErrorMessages.cs`. Never hardcode "not found" strings.
- **Magic numbers:** Use `AppConstants.MaxSubHabits`, `AppConstants.MaxUserFacts`, etc. from `Common/AppConstants.cs`. Never inline limits.
- **Cache invalidation:** Use `CacheInvalidationHelper.InvalidateSummaryCache(cache, userId)` after any habit mutation. Never manually write the 6-line cache removal loop.
- **PayGate propagation:** Use `result.PropagateError<T>()` from `Common/ResultExtensions.cs` to propagate PayGate failures. Never manually check `ErrorCode == "PAY_GATE"` with ternary.
- **PayGate controller responses:** Use `result.ToPayGateAwareResult(v => Ok(v))` from `Extensions/ResultActionResultExtensions.cs` in controllers. Never manually write the 403/PAY_GATE response block.
- **Shared validation:** `SharedHabitRules` in `Habits/Validators/` has `AddTitleRules()` and `AddDaysRules()` shared between Create and Update validators.

## Logging

- All controllers inject `ILogger<T>` and log business events
- Format: `logger.LogInformation("Action {Property}", value)` -- structured properties in PascalCase, English only
- **Auth:** log code sends, login success/failure, Google auth
- **Habits:** log create, delete, bulk operations
- **Tags:** log create, update, delete operations
- **Email:** log send success/failure with status codes (ResendEmailService)
- **Payments:** log checkout creation, webhook events
- **Validation:** log failed fields and endpoint path
- **Profile:** log timezone/language changes

## Security

- **Stripe API key:** Set once globally in `Program.cs` at startup. Never set `StripeConfiguration.ApiKey` per-request in controllers.
- **Webhook verification:** Stripe webhooks MUST verify signatures. Reject if `WebhookSecret` is not configured.
- **Security headers:** `SecurityHeadersMiddleware` adds nosniff, DENY, referrer-policy, XSS headers to all responses.
- **CORS:** Restricted to explicit methods (GET/POST/PUT/DELETE/PATCH) and headers (Authorization, Content-Type). No `AllowAnyHeader()`/`AllowAnyMethod()`.
- **Request size:** 10MB global Kestrel limit. Chat endpoint has its own 20MB multipart limit.
- **Input validation:** Validate Stripe checkout intervals against whitelist. Validate chat history size before JSON deserialization.

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

### Best Approach Only -- No Workarounds
- **ALWAYS use the best possible approach.** Never settle for workarounds, hacks, or "good enough" solutions. If the ideal approach exists, use it.
- Find root causes. No temporary fixes. No band-aids. Senior developer standards.
- Make every change as simple as possible. Impact minimal code.
- If a workaround is tempting, STOP and find the proper solution. Ask if unsure.

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

- Integration tests (xUnit + FluentAssertions) + unit tests (Domain, Application, Infrastructure test projects)
- Run: `dotnet test`
- **Every new feature must include unit tests** covering commands, queries, validators, and domain logic
- Test accounts: `REVIEWER_TEST_EMAIL`/`REVIEWER_TEST_CODE` and `QA_TEST_EMAIL`/`QA_TEST_CODE` env vars bypass email verification for testing
- Tests hit a real database -- never mock the DB layer
- Tests run sequentially (no parallel test execution)
