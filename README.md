# Orbit API

The REST API of Orbit, an AI habit tracker. It runs at [api.useorbit.org](https://api.useorbit.org). It serves the web app, the Android app and the MCP clients of Orbit.

The API uses Clean Architecture and CQRS. The server is the source of truth for habit schedules, streaks, entitlements and the user date.

## Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10, C# 14 |
| Database | PostgreSQL through EF Core 10 (Npgsql) |
| CQRS | MediatR 14 |
| Validation | FluentValidation 12 |
| Auth | JWT Bearer, email verification codes, Google OAuth |
| AI | OpenAI .NET SDK 2.15.0 (Responses API), model `gpt-6-luna` at low reasoning |
| Email | Amazon SES |
| Push | Firebase Admin SDK (FCM) and Lib.Net.Http.WebPush (VAPID) |
| Payments | Stripe.net 52 (web) and Google Play Billing (Android) |
| Storage | Private Amazon S3 bucket |
| Errors | Sentry |
| Tests | xUnit and FluentAssertions |

## Run the API

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a PostgreSQL database. Copy `.env.example` to `.env`. Put your database settings and API keys in `.env` or in a local development settings file.

```bash
dotnet ef database update --project src/Orbit.Infrastructure --startup-project src/Orbit.Api
dotnet run --project src/Orbit.Api
```

The API listens at `http://localhost:5000`. In development, the API documentation is at `/scalar`.

The settings come from `src/Orbit.Api/appsettings.json`. A local file or an environment variable overrides each secret. The separate OpenAI key for evaluation accounts and its daily spend limit are in [infra/README.md](infra/README.md#ai-cost-controls-and-evaluation-credentials).

## Run the tests

```bash
dotnet test
```

`Orbit.slnx` holds five test projects: `Orbit.Domain.Tests`, `Orbit.Application.Tests`, `Orbit.Infrastructure.Tests`, `Orbit.Analyzers.Tests` and `Orbit.Memory.Tests`. Some cases need a PostgreSQL database. [tests/CLAUDE.md](tests/CLAUDE.md) lists the test accounts and the run commands.

## Documents

- [CLAUDE.md](CLAUDE.md): the architecture and the rules for each change.
- [TESTING.md](TESTING.md): the offline AI checks, the cost estimate rule and the evaluation key rules.
- [infra/README.md](infra/README.md): the infrastructure, the release and the operation runbooks.
- [src/Orbit.Api/OpenApi/README.md](src/Orbit.Api/OpenApi/README.md): the published OpenAPI documents and the wire contracts.
- [src/Orbit.Api/OpenApi/AnswerComposition.md](src/Orbit.Api/OpenApi/AnswerComposition.md): the AI answer composition contract.
- [src/Orbit.Api/OpenApi/HabitRanking.md](src/Orbit.Api/OpenApi/HabitRanking.md): the habit ranking contract for clients.
- [src/Orbit.Api/OpenApi/ServerConversations.md](src/Orbit.Api/OpenApi/ServerConversations.md): the AI conversation storage, retention, export and account erasure rules.

## Related repositories

| Repository | Content |
|---|---|
| [orbit-ui-mobile](https://github.com/thomasluizon/orbit-ui-mobile) | The Next.js web app, the Expo mobile app and `@orbit/shared` |
| [orbit-landing-page](https://github.com/thomasluizon/orbit-landing-page) | The marketing landing page |

This project is private.
