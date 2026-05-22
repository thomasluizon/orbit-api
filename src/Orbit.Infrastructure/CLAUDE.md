# Orbit.Infrastructure — persistence + external services

EF Core + PostgreSQL + service integrations (OpenAI, Firebase, Stripe, Resend, VAPID, Supabase Auth).

## Layout

```
Persistence/
  OrbitDbContext.cs       - DbContext root
  Configurations/         - IEntityTypeConfiguration<T> per entity (Fluent API)
  Repositories/           - generic repo + per-aggregate repos
  UnitOfWork.cs
Migrations/               - EF migrations (alphabetical timestamped)
Services/                 - implementations of Orbit.Domain.Interfaces.* (AI, push, email, JWT, ...)
  AI/                     - OpenAI prompts + structured output handling
  Prompts/                - composable prompt sections (HabitCountSection, RoutinePatternsSection, ...)
Configuration/            - strongly-typed options bound from appsettings (ResendSettings, VapidSettings, ...)
AI/                       - top-level AI service entry points
```

## EF Core

- **Configuration via Fluent API**, never data annotations on entities. Every entity needs a `IEntityTypeConfiguration<T>` in `Persistence/Configurations/`.
- **DbContext is the only thing that knows about EF.** Repositories abstract it. Application code MUST NOT take a `DbContext` dependency.
- **No `[Required]` / `[StringLength]` / `[Key]` attributes on domain entities** — domain stays EF-free.
- **Migrations** are alphabetical-by-timestamp. Add via `dotnet ef migrations add <Name> --project src/Orbit.Infrastructure --startup-project src/Orbit.Api`. NEVER edit a migration that has been applied to any environment.
- **Adding a `DbSet<>` requires a FluentConfig.** If you forget the config, EF infers — which is wrong, and the next migration will be ugly.

## AI services (OpenAI)

- `OpenAI .NET SDK 2.8.0`, model `gpt-4.1-mini` by default.
- Prompts composed from `Services/Prompts/Sections/*` — dynamic sections (HabitCountSection, TodayDateSection, RoutinePatternsSection) compose at runtime based on user state.
- Structured output via JSON schema — define the response type, the SDK enforces shape.
- Cache results in `IMemoryCache` keyed by user + date + version. Invalidate via `CacheInvalidationHelper` in Application (never directly here).

## JWT

- `ITokenService` issues + verifies. Uses HS256 with a configured secret from `appsettings`/env.
- Refresh tokens stored in DB; access tokens stateless.
- Token lifetime: short access (15min-ish), long refresh (configured).

## Stripe

- **API key set ONCE** in `Program.cs` at startup: `StripeConfiguration.ApiKey = ...`. NEVER set it per-request in a controller.
- **Webhook signature verification** in `SubscriptionController.Webhook`. Reject if `WebhookSecret` is not configured.
- Validate checkout intervals against a whitelist of allowed values.

## Push notifications

- `PushNotificationService` routes by subscription type: `p256dh == "fcm"` → Firebase Admin SDK (native); otherwise → VAPID Web Push (browser).
- `ReminderSchedulerService` (BackgroundService) runs every 1 minute, finds habits with `ReminderEnabled && DueTime != null`, sends push + creates in-app notification. `SentReminder` table prevents duplicates.

## Email (Resend)

- `ResendEmailService` sends transactional emails (login codes, reset, support).
- Log success/failure with status codes — failures are observable in Render logs.

## Configuration

- Strongly-typed options pattern. `ResendSettings`, `VapidSettings`, `JwtSettings`, etc.
- Bound from `appsettings.json` + env vars in `Program.cs` via `services.Configure<X>(...)`.
- Secrets MUST come from env vars in production. Never commit `appsettings.Development.json`.

## Patterns to mirror

| Want to add… | Look at… |
|---|---|
| New entity persistence | `Persistence/Configurations/HabitConfiguration.cs` |
| New repository | `Persistence/Repositories/HabitRepository.cs` |
| New migration | run the dotnet ef CLI; example in root CLAUDE.md |
| New AI prompt | `Services/Prompts/Sections/*` |
| New external service | `Services/` + interface in `Orbit.Domain/Interfaces/` |
| New config block | `Configuration/ResendSettings.cs` + binding in `Program.cs` |
