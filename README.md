# Orbit API

The backend for **Orbit** -- an AI-powered habit tracker. Provides a REST API for habit management, AI chat, push notifications, and subscription billing. Built with Clean Architecture and CQRS.

**Live:** [api.useorbit.org](https://api.useorbit.org) | **App:** [app.useorbit.org](https://app.useorbit.org) | **Landing:** [useorbit.org](https://useorbit.org)

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | [.NET 10.0](https://dotnet.microsoft.com), C# 13 |
| Database | [PostgreSQL](https://www.postgresql.org) via [EF Core 10](https://learn.microsoft.com/ef/core) (Npgsql) |
| CQRS | [MediatR 14](https://github.com/jbogard/MediatR) |
| Validation | [FluentValidation 12](https://docs.fluentvalidation.net) |
| Auth | JWT Bearer + [Supabase Auth](https://supabase.com/auth) (Google OAuth) |
| AI | [Gemini 2.5 Flash](https://ai.google.dev) (primary), [Ollama](https://ollama.com) phi3.5:3.8b (fallback) |
| Email | [Resend](https://resend.com) (verification codes) |
| Push | [Firebase Admin SDK 3.5](https://firebase.google.com/docs/admin/setup) (FCM) + [WebPush](https://github.com/nichelaboratory/Lib.Net.Http.WebPush) (VAPID) |
| Payments | [Stripe](https://stripe.com) (checkout sessions, webhooks) |
| Password Hashing | BCrypt |
| API Docs | [Scalar](https://scalar.com) (dev only) |
| Testing | [xUnit](https://xunit.net) + FluentAssertions |
| Containerization | Docker (multi-stage build) |

## Features

- **Habit CRUD** -- Create, update, delete, duplicate, reorder habits with sub-habit support
- **Smart Scheduling** -- Server-side frequency calculations (daily, weekly, monthly, yearly, every N days) with day-of-week filtering and overdue detection
- **Completion Logging** -- Toggle completion per day with optional notes, full log history
- **Metrics** -- Current/longest streaks, weekly/monthly completion rates
- **AI Chat** -- Multi-turn conversation with Gemini for creating habits, logging completions, analyzing progress, suggesting breakdowns, assigning tags, and smart rescheduling. Supports image upload
- **AI Daily Summary** -- Cached, timezone-aware daily summaries invalidated on habit changes
- **Tags** -- Colored tags for habit organization
- **User Facts** -- Personal context facts that enhance AI responses (soft-deleted)
- **Push Notifications** -- Dual delivery: FCM for native Android, VAPID Web Push for browsers. Background scheduler checks due habits every minute
- **Email Verification** -- Passwordless code-based login via Resend
- **Google OAuth** -- Via Supabase Auth access tokens
- **Subscription Billing** -- Stripe checkout sessions with webhook processing
- **Timezone Support** -- All user-facing dates use timezone-aware "today" based on user profile
- **Pagination** -- All list endpoints paginated with `PaginatedResponse<T>`
- **Bulk Operations** -- Bulk create and delete habits
- **Notifications** -- In-app notification system with read/unread tracking

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- [PostgreSQL](https://www.postgresql.org/download/) (or [Supabase](https://supabase.com) for hosted)
- [Docker](https://www.docker.com/) (optional, for containerized deployment)

## Getting Started

```bash
# Clone the repository
git clone https://github.com/thomasluizon/orbit-api.git
cd orbit-api

# Copy environment config
cp .env.example .env
# Edit .env with your database credentials and API keys

# Apply database migrations
dotnet ef database update --project src/Orbit.Infrastructure --startup-project src/Orbit.Api

# Run the API
dotnet run --project src/Orbit.Api
```

The API runs at `http://localhost:5000`. API docs available at `/scalar` in development.

### Environment Variables

| Variable | Description |
|----------|-------------|
| `POSTGRES_USER` | PostgreSQL username |
| `POSTGRES_PASSWORD` | PostgreSQL password |
| `AI_PROVIDER` | `Gemini` or `Ollama` |
| `GEMINI_API_KEY` | Google Gemini API key |
| `GEMINI_MODEL` | Gemini model name (e.g., `gemini-2.5-flash`) |
| `CORS_ORIGIN` | Allowed frontend origin |
| `JWT_SECRET_KEY` | 64-char random string for JWT signing |
| `JWT_PREVIOUS_SECRET_KEY` | (optional) Previous signing key, accepted during validation only. Used for rotation. |
| `JWT_ISSUER` | JWT issuer claim |
| `JWT_AUDIENCE` | JWT audience claim |

Additional env vars for Supabase, Firebase, Stripe, VAPID, and Resend are configured in the hosting dashboard.

### Rotating the JWT signing key (zero-downtime)

The API supports a primary signing key plus an optional secondary validation
key. New tokens are always signed with `Jwt:SecretKey`; tokens signed with
`Jwt:PreviousSecretKey` continue to validate during the rollover window.

Procedure:

1. Generate a new 64-char secret. Set `Jwt:PreviousSecretKey` to the **current**
   `Jwt:SecretKey` value (so already-issued tokens keep working).
2. Set `Jwt:SecretKey` to the new value.
3. Deploy. Both keys are now accepted on validation.
4. Wait long enough for every issued token to expire (default `Jwt:ExpiryHours = 168` = 7 days).
5. Remove `Jwt:PreviousSecretKey`. Deploy again.

Never publish `Jwt:PreviousSecretKey` to clients — it is a server-only
validation hint. The rotation never re-issues active tokens; it just guarantees
neither old nor new tokens get rejected during the window.

## Architecture

Clean Architecture with four layers:

```
src/
  Orbit.Api/              # Presentation layer
    Controllers/          #   10 controllers (Auth, Chat, Config, Habits,
                          #   Notification, Profile, Subscription,
                          #   Support, Tags, UserFacts)
    Extensions/           #   PayGate-aware IActionResult helpers
    Middleware/            #   Security headers, validation exception handler
    Program.cs            #   DI configuration, middleware pipeline

  Orbit.Application/      # Application layer (CQRS)
    Common/               #   PaginatedResponse<T>, ErrorMessages, AppConstants,
                          #   CacheInvalidationHelper, PayGateService
    Habits/
      Commands/           #   Create, Update, Delete, Log, BulkCreate, BulkDelete,
                          #   Reorder, Duplicate, MoveToParent, CreateSubHabit
      Queries/            #   GetHabitSchedule (paginated), GetById, GetLogs, GetMetrics
      Services/           #   HabitScheduleService (frequency/schedule calculations)
      Validators/         #   SharedHabitRules, Create/Update/Log validators
    Chat/Commands/        #   ProcessUserChat (multi-turn AI orchestration)
    Tags/                 #   CRUD commands and queries
    UserFacts/            #   CRUD with soft delete
    Auth/                 #   SendCode, VerifyCode, GoogleAuth
    Profile/              #   Get, Update, Timezone, Onboarding, AI toggles
    Notification/         #   List, Read, Delete, Subscribe, Unsubscribe, TestPush
    Support/              #   SendSupportEmail
    Subscription/         #   CreateCheckout, HandleWebhook

  Orbit.Domain/           # Domain layer (zero dependencies)
    Entities/             #   User, Habit, HabitLog, Tag, UserFact, Notification,
                          #   PushSubscription, SentReminder, AppConfig
    Enums/                #   HabitFrequency, AiActionType, FrequencyUnit, etc.
    Interfaces/           #   IGenericRepository<T>, IUnitOfWork, IAiIntentService,
                          #   IUserDateService, IPushNotificationService, etc.
    Common/               #   Entity base class, Result<T> pattern
    Models/               #   AiAction, AiActionPlan (records)

  Orbit.Infrastructure/   # Infrastructure layer
    Persistence/          #   OrbitDbContext, GenericRepository<T>, UnitOfWork
    Services/             #   GeminiAiService, OllamaAiService, ResendEmailService,
                          #   PushNotificationService, ReminderSchedulerService,
                          #   JwtService, UserDateService
    Migrations/           #   20+ EF Core migrations

tests/
  Orbit.IntegrationTests/ # xUnit + FluentAssertions (real DB + Gemini API)
```

### Key Patterns

- **Result\<T\>** -- All handlers return `Result<T>` instead of throwing exceptions
- **CQRS** -- Strict command/query separation via MediatR
- **Factory methods** -- Entities created via `Entity.Create()` static methods
- **Generic repository + Unit of Work** -- Abstracted data access
- **PayGate** -- Subscription-gated features with `PayGateService` and propagation helpers
- **Validation pipeline** -- `ValidationBehavior<TRequest, TResponse>` in MediatR pipeline
- **Cache invalidation** -- AI summary cache cleared on any habit mutation

## API Endpoints

### Habits

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/habits?dateFrom=&dateTo=&includeOverdue=&search=&frequencyUnit=&isCompleted=&page=&pageSize=` | Paginated list with `scheduledDates[]` and `isOverdue` |
| GET | `/api/habits/{id}` | Habit detail with anchor date |
| POST | `/api/habits` | Create habit |
| PUT | `/api/habits/{id}` | Update habit |
| DELETE | `/api/habits/{id}` | Delete habit |
| POST | `/api/habits/{id}/log` | Toggle completion for today |
| GET | `/api/habits/{id}/logs` | Completion log history |
| GET | `/api/habits/{id}/metrics` | Streaks and completion rates |
| POST | `/api/habits/bulk` | Bulk create |
| DELETE | `/api/habits/bulk` | Bulk delete |
| PUT | `/api/habits/reorder` | Reorder positions |
| PUT | `/api/habits/{id}/parent` | Move to new parent |
| POST | `/api/habits/{parentId}/sub-habits` | Create sub-habit |
| POST | `/api/habits/{id}/duplicate` | Duplicate habit |
| GET | `/api/habits/summary` | AI-generated daily summary (cached) |

### Auth

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/auth/send-code` | Send email verification code |
| POST | `/api/auth/verify-code` | Verify code and get JWT |
| POST | `/api/auth/google` | Google OAuth via Supabase token |

### Profile

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/profile` | Get user profile |
| PUT | `/api/profile` | Update profile |
| PUT | `/api/profile/timezone` | Set timezone |
| PUT | `/api/profile/onboarding` | Mark onboarding complete |
| PUT | `/api/profile/ai-memory` | Toggle AI memory |
| PUT | `/api/profile/ai-summary` | Toggle daily summary |

### Chat, Tags, User Facts, Notifications, Subscription

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/chat` | AI chat (multipart, supports images) |
| GET/POST | `/api/tags` | List / create tags |
| PUT/DELETE | `/api/tags/{id}` | Update / delete tag |
| GET/POST | `/api/user-facts` | List / create user facts |
| PUT/DELETE | `/api/user-facts/{id}` | Update / delete user fact |
| GET | `/api/notification` | List notifications (last 50 + unread count) |
| PUT | `/api/notification/{id}/read` | Mark as read |
| PUT | `/api/notification/read-all` | Mark all as read |
| DELETE | `/api/notification/{id}` | Delete notification |
| POST | `/api/notification/subscribe` | Register push subscription |
| POST | `/api/notification/unsubscribe` | Remove push subscription |
| POST | `/api/notification/test-push` | Send test push notification |
| POST | `/api/subscription/checkout` | Create Stripe checkout session |
| POST | `/api/subscription/webhook` | Stripe webhook handler |
| GET | `/health` | Health check |

## Push Notifications

Dual delivery system via `PushNotificationService`:

- **FCM** -- Firebase Admin SDK for native Android. Subscriptions with `p256dh == "fcm"` route through Firebase
- **Web Push** -- VAPID-based for browsers. Uses `Lib.Net.Http.WebPush`
- **Scheduler** -- `ReminderSchedulerService` (BackgroundService) runs every minute, checks habits with `ReminderEnabled && DueTime != null`, sends push + creates in-app notification. `SentReminder` table prevents duplicates

## Testing

```bash
# Run integration tests (requires real DB + Gemini API)
dotnet test tests/Orbit.IntegrationTests
```

Tests use xUnit with FluentAssertions and hit the real database and Gemini API (sequential execution).

## Deployment

| Component | Service |
|-----------|---------|
| Hosting | [Render](https://render.com) (Docker, auto-deploy on push to `main`) |
| Database | [Supabase](https://supabase.com) PostgreSQL (session pooler) |
| Domain | `api.useorbit.org` |
| Push | Firebase project `orbit-11d4a` (FCM) |
| Email | [Resend](https://resend.com) |
| Payments | [Stripe](https://stripe.com) |

### Docker

```bash
# Build and run with Docker Compose
docker compose up -d --build
```

The Dockerfile uses a multi-stage build (SDK for build, ASP.NET runtime for production) and runs as a non-root user.

## Related Repositories

| Repo | Description |
|------|-------------|
| [orbit-ui](https://github.com/thomasluizon/orbit-ui) | Nuxt 4 frontend (web + Android) |
| [orbit-landing-page](https://github.com/thomasluizon/orbit-landing-page) | Marketing landing page |

## License

Private project.
