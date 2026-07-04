# Orbit API

The backend for **Orbit** -- an AI-powered habit tracker. Provides a REST API for habit management, an AI agent (chat + MCP tools), gamification, goals, calendar sync, social/accountability, push notifications, and subscription billing. Built with Clean Architecture and CQRS.

**Live:** [api.useorbit.org](https://api.useorbit.org) | **App:** [app.useorbit.org](https://app.useorbit.org) | **Landing:** [useorbit.org](https://useorbit.org)

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | [.NET 10.0](https://dotnet.microsoft.com), C# 13 |
| Database | [PostgreSQL](https://www.postgresql.org) via [EF Core 10](https://learn.microsoft.com/ef/core) (Npgsql) |
| CQRS | [MediatR 14](https://github.com/jbogard/MediatR) |
| Validation | [FluentValidation 12](https://docs.fluentvalidation.net) |
| Auth | JWT Bearer + email verification codes + Google OAuth ([Supabase](https://supabase.com) tokens) |
| AI | [OpenAI](https://platform.openai.com) — `gpt-4.1-mini` (primary), `gpt-5.4-nano` (sub-tasks), via the OpenAI .NET SDK |
| Email | [Resend](https://resend.com) (verification codes, contacts) |
| Push | [Firebase Admin SDK](https://firebase.google.com/docs/admin/setup) (FCM, native Android) + [WebPush](https://github.com/nichelaboratory/Lib.Net.Http.WebPush) (VAPID, browsers) |
| Payments | [Stripe](https://stripe.com) (web) + Google Play Billing (native Android) |
| Storage | [Supabase](https://supabase.com) object storage (`uploads` bucket) |
| Password/Token Hashing | BCrypt |
| Observability | [Sentry](https://sentry.io) |
| API Docs | [Scalar](https://scalar.com) (dev only) |
| Testing | [xUnit](https://xunit.net) + FluentAssertions |
| Containerization | Docker (multi-stage build) |

## Features

- **Habit CRUD** -- Create, update, delete, duplicate, reorder habits with sub-habit support
- **Smart Scheduling** -- Server-side frequency calculations (daily, weekly, monthly, yearly, every N days) with day-of-week filtering and overdue detection
- **Completion Logging** -- Toggle completion per day with optional notes, full log history
- **Metrics** -- Current/longest streaks, weekly/monthly completion rates
- **AI Agent** -- Multi-turn OpenAI-backed chat that creates habits, logs completions, analyzes progress, suggests breakdowns, assigns tags, and reschedules. Exposed to external clients as MCP tools with per-tool ownership/policy scoping and audit logging. Supports image upload
- **AI Assists** -- Cached daily summaries, retrospectives, fact extraction, goal review, habit/tag/reschedule suggestions, proactive check-ins, slip alerts, and batched usage summaries
- **Gamification** -- XP, levels, streaks, streak freezes, and achievements
- **Goals** -- Goal tracking with deadlines and AI-assisted review
- **Calendar** -- Google Calendar auto-sync of scheduled habits
- **Social & Accountability** -- Friends, public profiles, accountability partners, challenges, and referrals
- **Checklist Templates** -- Reusable habit/checklist templates
- **Tags** -- Colored tags for habit organization
- **User Facts** -- Personal context facts that enhance AI responses (soft-deleted)
- **Push Notifications** -- Dual delivery: FCM for native Android, VAPID Web Push for browsers, plus background schedulers for reminders, goal deadlines, slip alerts, and check-ins
- **Email Verification** -- Passwordless code-based login via Resend
- **Subscription Billing** -- Stripe (web) and Google Play Billing (native) with webhook / RTDN processing; backend is the source of truth for entitlements
- **Waitlist** -- Signed-token waitlist confirmation flow
- **Sync** -- Batched pull/mutation sync endpoints for clients
- **Timezone Support** -- All user-facing dates use timezone-aware "today" based on user profile
- **Pagination** -- List endpoints paginated with `PaginatedResponse<T>`

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
# Edit .env / appsettings.Development.json with your database credentials and API keys

# Apply database migrations
dotnet ef database update --project src/Orbit.Infrastructure --startup-project src/Orbit.Api

# Run the API
dotnet run --project src/Orbit.Api
```

The API runs at `http://localhost:5000`. API docs available at `/scalar` in development.

### Configuration

Settings are bound from `appsettings.json`, with secrets overridden in `appsettings.Development.json` locally and environment variables in production. Key sections:

| Section | Purpose |
|---------|---------|
| `ConnectionStrings:DefaultConnection` / `SessionConnection` | PostgreSQL (direct + session pooler) |
| `AI:ApiKey` / `AI:Model` / `AI:SubTaskModel` / `AI:BaseUrl` | OpenAI credentials and models (`BaseUrl` defaults to `https://api.openai.com/v1`) |
| `Jwt:SecretKey` / `Issuer` / `Audience` | JWT signing and claims |
| `Supabase:Url` / `AnonKey` / `SecretKey` / `Bucket` | Supabase auth tokens + object storage |
| `Resend:ApiKey` / `FromEmail` | Transactional email |
| `Vapid:PublicKey` / `PrivateKey` / `Subject` | Web Push (VAPID) |
| `Encryption:Key` | At-rest field encryption |
| `Google:ClientId` / `ClientSecret` | Google OAuth + Calendar |
| `Stripe:SecretKey` / `WebhookSecret` / price IDs | Stripe billing |
| `Cors:AllowedOrigins` / `LandingOrigins` | Allowed frontend origins |
| `Sentry:Dsn` / `Environment` | Error monitoring |

## Architecture

Clean Architecture with four layers:

```
src/
  Orbit.Api/              # Presentation layer
    Controllers/          #   26 controllers (see below)
    Extensions/           #   PayGate-aware IActionResult helpers
    Middleware/           #   Security headers, unhandled/validation exception handlers
    Program.cs            #   DI configuration, middleware pipeline

  Orbit.Application/      # Application layer (CQRS)
    Common/               #   PaginatedResponse<T>, ErrorMessages, AppConstants, PayGateService, ...
    Habits/               #   Commands / Queries / Validators / Services (HabitScheduleService)
    Chat/                 #   AI chat orchestration
    Goals/ Gamification/  #   Goals, XP/streaks/achievements
    Calendar/             #   Google Calendar sync
    Accountability/ Social/ Challenges/ Referrals/   # Social graph
    ChecklistTemplates/ Tags/ UserFacts/ Uploads/    # Content
    Auth/ Profile/ ApiKeys/ Subscriptions/ Support/ Notifications/ Waitlist/
    Behaviors/            #   MediatR pipeline (ValidationBehavior, ...)

  Orbit.Domain/           # Domain layer (zero dependencies)
    Entities/ Enums/ Interfaces/ Common/ Models/     # Entities, Result<T>, repository/service contracts

  Orbit.Infrastructure/   # Infrastructure layer
    Persistence/          #   OrbitDbContext, GenericRepository<T>, UnitOfWork
    Services/             #   AI (AiIntentService, AiSummaryService, OpenAiBatchPollerService, ...),
                          #   Agent/MCP (AgentCatalogService, AgentOperationExecutor, AgentPolicyEvaluator, ...),
                          #   billing (StripeBillingService, GooglePlayBillingService),
                          #   push + schedulers (PushNotificationService, ReminderSchedulerService, ...),
                          #   auth (JwtTokenService, GoogleTokenService), UserDateService, EncryptionService
    Migrations/           #   EF Core migrations

tests/
  Orbit.Domain.Tests/         # xUnit + FluentAssertions
  Orbit.Application.Tests/    # xUnit + FluentAssertions
  Orbit.Infrastructure.Tests/ # xUnit + FluentAssertions
```

### Controllers

`Accountability`, `Achievements`, `Ai`, `ApiKeys`, `Auth`, `Calendar`, `Challenges`, `Chat`, `ChecklistTemplates`, `Config`, `Friends`, `Gamification`, `Goals`, `Habits`, `Notification`, `OAuth`, `Profile`, `PublicProfile`, `Referral`, `Subscription`, `Support`, `Sync`, `Tags`, `Uploads`, `UserFacts`, `Waitlist`.

### Key Patterns

- **Result\<T\>** -- Handlers return `Result<T>` instead of throwing for expected failures
- **CQRS** -- Command/query separation via MediatR, one folder per feature
- **Factory methods** -- Entities created via `Entity.Create()` static methods
- **Generic repository + Unit of Work** -- Abstracted data access
- **PayGate** -- Subscription-gated features via `PayGateService` and `Result` propagation helpers
- **Validation pipeline** -- `ValidationBehavior<TRequest, TResponse>` in the MediatR pipeline
- **Cache invalidation** -- AI summary cache cleared on any habit mutation

## Push Notifications

Dual delivery via `PushNotificationService`:

- **FCM** -- Firebase Admin SDK for native Android. Subscriptions with `p256dh == "fcm"` route through Firebase
- **Web Push** -- VAPID-based for browsers, via `Lib.Net.Http.WebPush`
- **Schedulers** -- Background services (`ReminderSchedulerService`, `GoalDeadlineNotificationService`, `SlipAlertSchedulerService`, `ProactiveCheckinSchedulerService`) send push + create in-app notifications; a `SentReminder` record prevents duplicates

## Testing

```bash
# Run all unit tests
dotnet test
```

Tests use xUnit with FluentAssertions. Unit tests only — there is no integration or E2E suite.

## Deployment

| Component | Service |
|-----------|---------|
| Hosting | [Render](https://render.com) (Docker, auto-deploy on push to `main`) |
| Database | [Supabase](https://supabase.com) PostgreSQL (session pooler) |
| Domain | `api.useorbit.org` |
| Push | Firebase project `orbit-11d4a` (FCM) |
| Email | [Resend](https://resend.com) |
| Payments | [Stripe](https://stripe.com) + Google Play Billing |
| Monitoring | [Sentry](https://sentry.io) |

### Docker

```bash
# Build and run with Docker Compose
docker compose up -d --build
```

The Dockerfile uses a multi-stage build (SDK for build, ASP.NET runtime for production) and runs as a non-root user.

## Related Repositories

| Repo | Description |
|------|-------------|
| [orbit-ui-mobile](https://github.com/thomasluizon/orbit-ui-mobile) | Turborepo frontend — `apps/web` (Next.js 16) + `apps/mobile` (Expo, Android) + `packages/shared` |
| [orbit-landing-page](https://github.com/thomasluizon/orbit-landing-page) | Marketing landing page |

## License

Private project.
