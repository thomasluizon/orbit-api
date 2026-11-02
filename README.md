# Orbit API

The backend for **Orbit** -- an AI-powered habit tracker. Provides a REST API for habit management, an AI agent (chat + MCP tools), gamification, goals, calendar sync, social features, push notifications, and subscription billing. Built with Clean Architecture and CQRS.

**Live:** [api.useorbit.org](https://api.useorbit.org) | **App:** [app.useorbit.org](https://app.useorbit.org) | **Landing:** [useorbit.org](https://useorbit.org)

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | [.NET 10.0](https://dotnet.microsoft.com), C# 13 |
| Database | [PostgreSQL](https://www.postgresql.org) via [EF Core 10](https://learn.microsoft.com/ef/core) (Npgsql) |
| CQRS | [MediatR 14](https://github.com/jbogard/MediatR) |
| Validation | [FluentValidation 12](https://docs.fluentvalidation.net) |
| Auth | JWT Bearer + email verification codes + Google OAuth authorization code exchange |
| AI | [OpenAI](https://platform.openai.com) via the Responses API and OpenAI .NET SDK 2.14.0; `gpt-6-luna` for chat and sub-tasks at low reasoning |
| Email | [Amazon SES](https://aws.amazon.com/ses/) (verification codes, contacts) |
| Push | [Firebase Admin SDK](https://firebase.google.com/docs/admin/setup) (FCM, native Android) + [WebPush](https://github.com/nichelaboratory/Lib.Net.Http.WebPush) (VAPID, browsers) |
| Payments | [Stripe](https://stripe.com) (web) + Google Play Billing (native Android) |
| Storage | Private [Amazon S3](https://aws.amazon.com/s3/) uploads bucket |
| Password/Token Hashing | BCrypt |
| Observability | [Sentry](https://sentry.io) |
| API Docs | [Scalar](https://scalar.com) (dev only) |
| Testing | [xUnit](https://xunit.net) + FluentAssertions |
| Containerization | Docker (multi-stage build) |

Astra's gated server conversation storage, private image continuity and account
retention contract are documented in
[ServerConversations.md](src/Orbit.Api/OpenApi/ServerConversations.md).

## Features

- **Habit CRUD** -- Create, update, delete, duplicate, reorder habits with sub-habit support
- **Smart Scheduling** -- Server-side frequency calculations (daily, weekly, monthly, yearly, every N days) with day-of-week filtering and overdue detection
- **Completion Logging** -- Toggle completion per day with optional notes, full log history
- **Metrics** -- Current/longest streaks, weekly/monthly completion rates
- **AI Agent** -- Multi-turn OpenAI-backed chat that creates habits, logs completions, analyzes progress, suggests breakdowns, assigns tags, and reschedules. Exposed to external clients as MCP tools with per-tool ownership/policy scoping and audit logging. Supports image upload. Provider tool schemas and the agent operation catalog share repeated blocks with local `$defs` and `$ref`; [offline cost and equivalence checks](TESTING.md#request-cache-and-cost-evidence) cover legacy, composition and offer clients.
- **AI Assists** -- Cached daily summaries, retrospectives, fact extraction, goal review, habit/tag/reschedule suggestions, proactive check-ins, slip alerts, and batched usage summaries
- **Gamification** -- XP, levels, streaks, streak freezes, and achievements
- **Goals** -- Goal tracking with deadlines and AI-assisted review
- **Calendar** -- Google Calendar auto-sync of scheduled habits
- **Social** -- Friends, public profiles, and referrals
- **Checklist Templates** -- Reusable habit/checklist templates
- **Tags** -- Colored tags for habit organization
- **Push Notifications** -- Dual delivery: FCM for native Android, VAPID Web Push for browsers, plus background schedulers for reminders, goal deadlines, slip alerts, and check-ins
- **Email Verification** -- Passwordless code-based login via Amazon SES
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
| `AI:EvalApiKey` / `AI:EvalDailyBudgetUsd` / `StagingEval:AccountIds` | Separate OpenAI key required for evaluation accounts, with a shared UTC daily spend ceiling; [setup and cost logs](infra/README.md#astra-cost-controls-and-evaluation-credentials) |
| `Jwt:SecretKey` / `Issuer` / `Audience` | JWT signing and claims |
| `Storage:S3:Bucket` / `Region` / `PublicBaseUrl` / `AccessKeyId` / `SecretAccessKey` | Required S3 upload, read, and erasure configuration |
| `Ses:AccessKeyId` / `SecretAccessKey` / `FromEmail` | Transactional email |
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
    Controllers/          #   Controllers (see below)
    Extensions/           #   PayGate-aware IActionResult helpers
    Middleware/           #   Security headers, unhandled/validation exception handlers
    Program.cs            #   DI configuration, middleware pipeline

  Orbit.Application/      # Application layer (CQRS)
    Common/               #   PaginatedResponse<T>, ErrorMessages, AppConstants, PayGateService, ...
    Habits/               #   Commands / Queries / Validators / Services (HabitScheduleService)
    Chat/                 #   AI chat orchestration
    Goals/ Gamification/  #   Goals, XP/streaks/achievements
    Calendar/             #   Google Calendar sync
    Referrals/            #   Referral rewards
    ChecklistTemplates/ Tags/ Uploads/    # Content
    Auth/ Profile/ ApiKeys/ Subscriptions/ Support/ Notifications/ Waitlist/
    Behaviors/            #   MediatR pipeline (ValidationBehavior, ...)

  Orbit.Domain/           # Domain layer (zero dependencies)
    Entities/ Enums/ Interfaces/ Common/ Models/     # Entities, Result<T>, repository/service contracts

  Orbit.Infrastructure/   # Infrastructure layer
    Persistence/          #   OrbitDbContext, GenericRepository<T>, UnitOfWork
    Services/             #   AI (AiIntentService, AiSummaryService, ...),
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

`Achievements`, `Ai`, `ApiKeys`, `Auth`, `Calendar`, `Chat`, `ChecklistTemplates`, `Config`, `Gamification`, `Goals`, `Habits`, `Notification`, `OAuth`, `Profile`, `Referral`, `Subscription`, `Support`, `Sync`, `Tags`, `Uploads`, `Waitlist`.

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

Astra's launch gate and offline replay procedure are documented in [TESTING.md](TESTING.md).

## Deployment

Astra composition is opt-in and independent of model configuration. See its
[producer contract](src/Orbit.Api/OpenApi/AnswerComposition.md) and
[activation runbook](infra/README.md#astra-answer-composition-activation).
Capability fallbacks use localized facts without repeated title headings; feature help stays
within its documented tool result. These corrections do not require global composition activation.

Habit ranking uses an API-first rollout with client capability negotiation. See the
[consumer contract](src/Orbit.Api/OpenApi/HabitRanking.md) and
[activation runbook](infra/README.md#habit-ranking-activation).

| Component | Service |
|-----------|---------|
| Hosting | [Render](https://render.com) (Docker, manual API releases) |
| Database | [Supabase](https://supabase.com) PostgreSQL (session pooler) |
| Domain | `api.useorbit.org` |
| Push | Firebase project `orbit-11d4a` (FCM) |
| Email | [Amazon SES](https://aws.amazon.com/ses/) |
| Payments | [Stripe](https://stripe.com) + Google Play Billing |
| Monitoring | [Sentry](https://sentry.io) |

Run `release.yml` from `main` with an `environment` of `production` or `staging` and a `branch` to release. Pass the planned full commit through `sha`; staging requires it, while production defaults to the dispatch commit. The workflow verifies checkout identity and selected-branch ancestry. Production accepts only `main` and promotes the immutable GHCR digest verified by staging for the selected commit. It does not build another image or run a Render pre-deploy command.

Staging builds `Dockerfile` at the verified selected SHA in GitHub Actions with the `docker` driver, then pushes `ghcr.io/thomasluizon/orbit-api:<sha>` using `GITHUB_TOKEN` with `packages: write`. The image carries that SHA in `RENDER_GIT_COMMIT`, which `/health` reports. The staging job deploys the pushed immutable digest through Render's `POST /v1/services/{serviceId}/deploys` with `imageUrl`. It requires HTTP 201 naming the same `image.ref` and a deploy ID, then polls that deploy to `live`. HTTP 202, a different image, an ambiguous write or a terminal failure stops the release. Inspect service deploys before rerunning an ambiguous request. The digest is recorded in the job summary before deployment, including during initial service setup. The package must be public so Render can pull it without a registry credential; see [staging source setup](infra/README.md#staging-api-image-source).

After staging health and live-image verification, the staging job records `payload.image_digest`
on its GitHub deployment for the selected commit, then records success. Production first
requires staging's public health to report the same commit as `Healthy`, then resolves
that commit's latest staging deployment record and requires a successful status, the
Actions bot creator and a valid immutable digest. Missing or legacy records require a new
staging release; production never resolves a mutable tag. `force_unstaged` is recorded in
the run summary and bypasses the current public staging health gate. It still requires a
successful recorded staging image for the selected commit, so it can promote a previously
verified image while staging is unavailable or serves another commit. Both jobs send
`imageUrl`, require HTTP 201 naming that image and poll its deploy to `live`.

Production and staging run migrations at startup with `Database__MigrateOnStartup=true`.
The migration completes before the server starts accepting traffic; failure prevents the
new instance from becoming healthy. Take a fresh completed Render database export before
every production release carrying migrations and record it in the release evidence, as
required by `.claude/specs/orbit-prod-release.md` in `orbit-ui-mobile`. The first production
image release also requires a fresh export. See the [production image cutover and migration
limits](infra/README.md#production-api-image-source) before the first image release or any
long migration.

Both jobs then poll public `/health` until it reports `Healthy` and the exact selected commit. Public verification has one 180-second budget, waits up to five seconds between probes, and caps each request at ten seconds or the remaining budget. Stale commits, unhealthy or invalid responses, and HTTP or network failures retry within that budget. The first match ends polling; the final Render identity check must also pass before recording the deployment: both jobs check the live image reference. On exhaustion, the job reports the expected SHA, last valid status and commit, probe count, elapsed time, and a sanitized result category without printing response bodies or credentials. Inspect those identifiers and the Render deploy logs before explicitly rerunning a failed release; verification never rolls back or redeploys automatically.

Set `RENDER_API_KEY` as an environment secret in each of the `production` and `staging` GitHub environments, and set the repository variables `RENDER_PRODUCTION_SERVICE_ID` and `RENDER_STAGING_SERVICE_ID`. Restrict the `production`, `staging`, and `render-operations` GitHub environments to deployment branch `main`. The staging reseed and Postgres access reconciliation workflows use the `render-operations` environment secret `RENDER_API_KEY` for database and service management. Remove the repository secret `RENDER_API_KEY` after both release paths and the operational workflows succeed.

Terraform declares `BotProtection__Enabled=true` and an environment-specific `BotProtection__AllowedHostnames__N` list in the Render API environment groups. Apply `render_env_group.staging_api` first, then `render_env_group.production_api` only after the deployed web and Android sign-in flows and landing waitlist send `turnstileToken`. Confirm the installed Android build supports the check and the production `MinSupportedVersion` floor covers the carrying build before production enablement. Follow the version-floor procedure in `AGENTS.md` for any raise.

Before release, take a fresh Render production database export. Apply only the API environment group target for each environment. Verify tokenless public send-code, verify-code and waitlist requests return 400, and token-bearing web, Android and waitlist requests succeed. Production must reject staging hostnames and staging must reject production hostnames. Browser-based acceptance checks are performed by the release owner.

Set `BotProtection__Enabled=false` in the affected Render API environment group to keep sign-in available during a Turnstile outage, then reconcile Terraform before the next apply. Production logs one startup warning while disabled. There is no email-based bot-check exemption. Waitlist confirmation links issued before the encrypted token format must be requested again through the join form.

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

## Data export and account erasure

The profile data export returns the account's portable data while the account exists.
Erasure removes the account and owned data. Provider cancellation obligations are billing
reconciliation data and survive erasure separately in `OwedSubscriptionCancellations`.
They cannot be joined back to an account by an Orbit user identifier and are not included
in the profile export, because they are created as the account is erased.

Each record keeps a random record ID, the provider (Stripe or Google Play), the Stripe
subscription ID or Play purchase token, the attempt count, creation time, last attempt time,
next attempt time, and exhaustion time. It keeps no name, email, Orbit user ID, payment card,
or provider error text. Purchase tokens and subscription IDs must be treated as sensitive
billing data; alerts use only the record ID, provider and attempt count.

After the seven-day billing deferral, erasure commits each retained provider identifier's
cancellation obligation in the same transaction that deletes the account. An error alert
reports erasure with unconfirmed renewals. The cancellation worker scans every minute in
both scheduler modes, independently of the erased account and any original queued job.
It backs off for one minute, five minutes, then one hour between attempts. After 27
attempts it continues attempting cancellation daily. A successful cancellation, an already
stopped renewal, or a provider-confirmed missing subscription removes the record immediately. A failed provider does not prevent
another provider's obligation from being processed.

Exhausted obligations remain until billing reconciliation confirms renewal has stopped;
there is no time-based expiry that could forget an active charge. Exhaustion emits an error
alert with the record ID and provider, repeated daily while unresolved. Billing operators
must locate that record, verify and stop renewal with the named provider, and remove the
record only after confirming cancellation or that the subscription no longer exists.
The next attempt time schedules both daily cancellation attempts and exhaustion reminders.
Each attempt is committed before provider calls, which run outside database transactions.
Interrupted attempts remain due for a retry after backoff. Provider cancellation
that succeeds before a database failure is safe to verify again on the next scan.
