# Orbit API

The REST API of Orbit. It runs at `api.useorbit.org`.

## Stack

.NET 10, C# 14, PostgreSQL through EF Core 10, MediatR 14, FluentValidation 12, JWT Bearer, OpenAI .NET SDK 2.15.0 (Responses API, GPT-6 Luna at low reasoning), Stripe.net 52, Firebase Admin SDK (FCM), Lib.Net.Http.WebPush (VAPID).

## Architecture

The four projects are `Orbit.Api` → `Orbit.Application` → `Orbit.Domain` ← `Orbit.Infrastructure`. MediatR runs the commands and the queries. Each feature has its own folder in `Orbit.Application`. Infrastructure holds a generic repository and a Unit of Work. `Orbit.Api`, `Orbit.Application` and `Orbit.Infrastructure` each have a `CLAUDE.md`. Read it before you edit that project.

- Build a domain entity only through its factory method, for example `Habit.Create()`. Give the entity no public setter.
- Keep Domain free of EF. Configure each entity in Infrastructure.

## Rules for all projects

These rules override a project rule that conflicts with them.

- **Timezone:** Get each user-facing date from `IUserDateService.GetUserTodayAsync(userId)`. The analyzer `ORBIT0004` fails CI on `DateOnly.FromDateTime(DateTime.UtcNow...)`.
- **Authorization:** A fallback policy requires an authenticated user on each endpoint. Mark a public endpoint with `[AllowAnonymous]` or `.AllowAnonymous()`.
- **Validation:** Add a validator in `Orbit.Application/<Feature>/Validators/` and a guard in the entity factory or update method. The backend is the source of truth.
- **Comments:** `ORBIT0001` fails CI on a narration comment. Keep only XML documentation and a WHY note with a URL. `dotnet format` fixes the rest.
- **Migrations:** A migration is append-only and idempotent. A raw `CREATE INDEX` needs `IF NOT EXISTS`. A raw `DROP INDEX` needs `IF EXISTS`. The Unit Tests job enforces this. The rolling migration rules are in `tests/Orbit.Infrastructure.Tests/Persistence/RollingMigrations.md`.

## Cross-repo parity

The TypeScript consumer is `thomasluizon/orbit-ui-mobile`. It holds the Next.js web app, the Expo mobile app and `@orbit/shared`. Update the consumer in the same session as the API change.

| API change | Consumer change |
|---|---|
| New endpoint | `packages/shared/src/api/endpoints.ts` constant, a Server Action (web), an apiClient call (mobile), and a query key if the endpoint reads |
| New DTO field | The Zod schema in `packages/shared/src/types/*.ts` |
| Renamed or removed field | The Zod schema and each callsite in both apps |

Make a DTO change append-only and deploy the API first. A breaking change uses expand-contract and `AppConfig.MinSupportedVersion`. The procedure to raise the floor is in `infra/minimum-supported-version.md`.

## Git workflow

- Name a branch `feature/`, `fix/` or `chore/`. Squash merge only. Never reuse a merged branch.
- Never use `--no-verify` or `--no-gpg-sign`.
- Write `Tracks thomasluizon/orbit-tickets#N` in the pull request body. Never write `Closes`.
- The server refuses an admin merge and a direct push to `main`. If a merge needs an override, stop and ask the owner.
- The Pullfrog prompt lives in its console. It changes only by an owner decision.
- `lefthook.yml` runs `dotnet format` and the dash check on the staged files. Run `lefthook install` once in each clone.

## Testing

- Use xUnit and FluentAssertions. Each new feature needs unit tests for its commands, queries, validators and domain logic.
- A handler test mocks at the ports. A relational query-shape test uses `SqliteOrbitDbContextFactory`. A test that needs Postgres behavior uses `tests/Orbit.Infrastructure.Tests/Persistence/PostgresTestDatabase.cs`.
- A pull request that changes `src/` runs two jobs: Unit Tests and Guards. The push to `main` also runs the Docker migration bundle and CodeQL.
- After a deterministic test failure, rerun only the failed job with `gh run rerun <run-id> --failed`.
- `tests/Orbit.Infrastructure.Tests/Persistence/QueryRoundTripCountTests.cs` guards against N+1 queries. Read `tests/CLAUDE.md` for the test accounts and the run commands.
