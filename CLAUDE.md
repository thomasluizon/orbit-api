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

### DRY Patterns (Application Layer)
- **Error messages:** Use `ErrorMessages.UserNotFound`, `ErrorMessages.HabitNotFound`, etc. from `Common/ErrorMessages.cs`. Never hardcode "not found" strings.
- **Magic numbers:** Use `AppConstants.MaxSubHabits`, `AppConstants.MaxUserFacts`, etc. from `Common/AppConstants.cs`. Never inline limits.
- **Cache invalidation:** Use `CacheInvalidationHelper.InvalidateSummaryCache(cache, userId)` after any habit mutation. Never manually write the 6-line cache removal loop.
- **PayGate propagation:** Use `result.PropagateError<T>()` from `Common/ResultExtensions.cs` to propagate PayGate failures. Never manually check `ErrorCode == "PAY_GATE"` with ternary.
- **PayGate controller responses:** Use `result.ToPayGateAwareResult(v => Ok(v))` from `Extensions/ResultActionResultExtensions.cs` in controllers. Never manually write the 403/PAY_GATE response block.
- **Shared validation:** `SharedHabitRules` in `Habits/Validators/` has `AddTitleRules()` and `AddDaysRules()` shared between Create and Update validators.

### Security
- **Stripe API key:** Set once globally in `Program.cs` at startup. Never set `StripeConfiguration.ApiKey` per-request in controllers.
- **Webhook verification:** Stripe webhooks MUST verify signatures. Reject if `WebhookSecret` is not configured.
- **Security headers:** `SecurityHeadersMiddleware` adds nosniff, DENY, referrer-policy, XSS headers to all responses.
- **CORS:** Restricted to explicit methods (GET/POST/PUT/DELETE/PATCH) and headers (Authorization, Content-Type). No `AllowAnyHeader()`/`AllowAnyMethod()`.
- **Request size:** 10MB global Kestrel limit. Chat endpoint has its own 20MB multipart limit.
- **Input validation:** Validate Stripe checkout intervals against whitelist. Validate chat history size before JSON deserialization.

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

### Notifications & Push
- `GET /api/notification` - List notifications (last 50 + unread count)
- `PUT /api/notification/{id}/read` - Mark as read
- `PUT /api/notification/read-all` - Mark all as read
- `DELETE /api/notification/{id}` - Delete notification
- `DELETE /api/notification/all` - Delete all
- `POST /api/notification/subscribe` - Register push subscription (FCM token or Web Push keys)
- `POST /api/notification/unsubscribe` - Remove subscription
- `POST /api/notification/test-push` - Send test notification (uses PushNotificationService, supports both FCM and Web Push)

### Auth
- `POST /api/auth/send-code` - Send verification code via email
- `POST /api/auth/verify-code` - Verify code and login
- `POST /api/auth/google` - Google OAuth (Supabase access token)

### Other
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
- **Auth:** Supabase Auth for Google OAuth, custom email/code verification via Resend
- **Domain:** api.useorbit.org
- **Firebase:** Project orbit-11d4a, FCM for native push notifications
- **Env vars:** Configured in Render dashboard (connection string, JWT, Supabase, Gemini, CORS, Firebase, Stripe, Vapid, Resend)
- **Observability:** Render built-in logs + metrics. Production log level: Information (Microsoft.AspNetCore/EF Core: Warning)

## Push Notifications
- **Dual delivery:** `PushNotificationService` routes by subscription type -- `p256dh == "fcm"` sends via Firebase Admin SDK, otherwise via Lib.Net.Http.WebPush (VAPID)
- **FCM:** Firebase Admin SDK initialized in Program.cs from `Firebase:CredentialsJson` env var. Handles `Unregistered`/`InvalidArgument` as stale tokens.
- **Web Push:** VAPID auth with keys from `Vapid:PublicKey`/`PrivateKey`/`Subject`. Handles `410 Gone`/`404 NotFound` as stale subscriptions.
- **Scheduler:** `ReminderSchedulerService` (BackgroundService) runs every 1 minute, checks habits with `ReminderEnabled && DueTime != null`, sends push + creates in-app notification. `SentReminder` table prevents duplicates per (habitId, date).
- **Test endpoint:** `POST /api/notification/test-push` uses PushNotificationService directly for diagnostic push.

## Git Workflow

Branch protection is enforced on `main`. No direct pushes, no force pushes, no branch deletion.

### Branching Convention

- `feature/xxx` -- new features
- `fix/xxx` -- bugfixes
- `chore/xxx` -- maintenance, config, docs

### Merge Strategy

- **Squash merge only** -- keeps `main` history linear and clean
- Squash commit uses PR title + PR body
- Head branches auto-delete after merge

### Workflow

```bash
# 1. Create branch from main
git checkout main && git pull
git checkout -b feature/my-change

# 2. Work and commit
git add <files> && git commit -m "description"

# 3. Push and create PR
git push -u origin feature/my-change
gh pr create --fill

# 4. Merge via squash
gh pr merge --squash
```

### Rules

- Never push directly to `main` -- always go through a PR
- Never force push to `main`
- Keep PRs focused: one feature or fix per PR
- Branch names should be descriptive: `feature/add-tags-to-habits`, `fix/login-redirect`

## Logging Convention
- All controllers inject `ILogger<T>` and log business events
- Format: `logger.LogInformation("Action {Property}", value)` -- structured properties in PascalCase, English only
- Auth: log code sends, login success/failure, Google auth
- Habits: log create, delete, bulk operations
- Tags: log create, update, delete operations
- Email: log send success/failure with status codes (ResendEmailService)
- Payments: log checkout creation, webhook events
- Validation: log failed fields and endpoint path
- Profile: log timezone/language changes
