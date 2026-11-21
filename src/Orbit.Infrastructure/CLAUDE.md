# Orbit.Infrastructure

EF Core, PostgreSQL and the external services: OpenAI, Firebase, Stripe, Amazon SES and VAPID.

## EF Core

- Configure each entity with the Fluent API in `OrbitDbContext.OnModelCreating`. Never use a data annotation on an entity.
- Add a Fluent configuration for each new `DbSet<>`. EF infers a wrong shape without it.
- Create a migration with `dotnet ef migrations add <Name> --project src/Orbit.Infrastructure --startup-project src/Orbit.Api`.
- Never edit a migration that any environment applied.
- Infrastructure owns the DbContext, the migrations and the provider setup. Application builds its queries on the repository `IQueryable`.

## Stripe

- Set `StripeConfiguration.ApiKey` once at startup, in `Orbit.Api/Extensions/ServiceCollectionExtensions.Infrastructure.cs`. Never set it in a controller.
- Verify the signature in `HandleWebhookCommandHandler` (`Orbit.Application/Subscriptions/Commands/HandleWebhookCommand.cs`). Reject the event when `WebhookSecret` is not set.
- Check a checkout interval against a list of allowed values.
- Each Stripe webhook endpoint keeps the API version of its creation. `StripeWebhookVersionHealthCheck` serves `/health/stripe`. Release API refuses a deploy when no enabled endpoint matches `Stripe.net`. See [Stripe webhook endpoint version](../../infra/README.md#stripe-webhook-endpoint-version).

## Secrets

Production secrets come from environment variables. Never commit `appsettings.Development.json`.
