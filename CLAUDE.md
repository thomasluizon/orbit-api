# Orbit API

.NET 10 / EF Core / MediatR CQRS REST API. Hosts at `api.useorbit.org`.

## Stack

.NET 10, C# 14 (net10.0's default language version), PostgreSQL via EF Core 10, MediatR 14, FluentValidation 12, JWT Bearer, OpenAI .NET SDK 2.14.0 Responses API with GPT-6 Luna for chat and sub-tasks at low reasoning, BCrypt, Firebase Admin SDK (FCM), Lib.Net.Http.WebPush (VAPID).

## Architecture (one-pager)

Clean architecture, four projects: `Orbit.Api` → `Orbit.Application` → `Orbit.Domain` ← `Orbit.Infrastructure`. CQRS via MediatR. Commands and queries live per-feature folder in `Orbit.Application`. Domain entities use factory methods (`Habit.Create()`, `User.Create()`). Generic repository + Unit of Work in Infrastructure. Each project has a scoped `CLAUDE.md` — read those when editing inside that project.

## Cross-cutting hard rules

These apply everywhere — they override project-local conventions if they conflict.

- **Timezone:** All user-facing dates MUST use `IUserDateService.GetUserTodayAsync(userId)`. `DateTime.UtcNow` is only for `*AtUtc` timestamp parameters, fields and properties (including `Add*` arithmetic on the instant and locals used exclusively by those sinks), cache keys, and `TimeZoneInfo` conversion into the user's timezone - Roslyn `ORBIT0004` (`src/Orbit.Analyzers`) blocks other raw `DateTime.UtcNow` reads in Api/Application/Infrastructure and blocks `DateOnly.FromDateTime(DateTime.UtcNow...)` in all four projects with no exemption.
- **Authorization:** default `[Authorize]`; Roslyn `ORBIT0003` (`src/Orbit.Analyzers`) fails the build on a Controller with no class-level `[Authorize]`/`[AllowAnonymous]` (per-action attributes on every action also satisfy it). Exempt (mark `[AllowAnonymous]`): `/health`, `/api/auth/*`, signature-verified webhooks.
- **Validation:** Every new feature needs validators in `Orbit.Application/<Feature>/Validators/` AND domain-entity guards in factory/update methods. The backend is the source of truth — never trust the frontend.
- **Logging levels.** Inject `ILogger<T>`; structured properties in PascalCase, English only (`logger.LogInformation("Action {Property}", value)`). Reserve each level so prod logs carry only signal:
  - **Trace/Debug** — routine per-operation success, AI call lifecycle + token usage, per-step progress, per-delivery sends. Filtered out in prod.
  - **Information** — low-frequency business/lifecycle events only: signup, subscription change, sync-completed-with-N (gate the log on `N > 0`), account deletion, streak-freeze activation, the daily AI-cost summary, service start/stop. Never internal steps.
  - **Warning** — recoverable/degraded paths (retries, empty AI response, best-effort metric-write failure).
  - **Error** — actionable failures. Let Sentry capture exceptions; don't add a redundant manual `LogError(ex)` that mirrors a captured exception — keep exactly one Sentry issue + one console error per unhandled exception (the `UnhandledExceptionHandler` console log is held off Sentry via its `Logging:Sentry.AspNetCore.SentryAspNetCoreLoggerProvider:LogLevel` category; Sentry 6.13.0 removed the `Sentry` alias from that provider, so a `Logging:Sentry` rule no longer reaches it).
  - Prod minimum levels (framework namespaces → `Warning`) live in `appsettings.Production.json`.
- **No workarounds.** Root-cause every bug. No `TODO`/`FIXME`/`HACK` in committed code. No empty `catch {}`. No `as any`-equivalents like unjustified `null!`.
- **No dead code.** Delete unused methods, types, parameters, branches the moment they become orphaned by your change.
- **Narration comments are `ORBIT0001` build errors** (`src/Orbit.Analyzers`; only `///`/`/** */` XML-doc or a URL-linked WHY note survive; autofix via `dotnet format`). The analyzer is silent in local builds but fails CI — grep for bare `//` before pushing.
- **Missing authorization attributes (`ORBIT0003`, Security) and raw `DateTime.UtcNow` for user-facing dates (`ORBIT0004`, Reliability) are build errors too** (`src/Orbit.Analyzers`). They are silent in local builds but fail CI; run `dotnet test tests/Orbit.Analyzers.Tests` for local proof. The timestamp exemption trusts a callee's `*AtUtc` parameter name and cannot inspect the callee body; that name must represent an instant, never a user-facing date. Other deliberate UTC-instant/UTC-window sites need targeted `#pragma warning disable ORBIT0004` blocks.

## Cross-repo parity contract

The TypeScript consumer is `thomasluizon/orbit-ui-mobile` (Turborepo: Next.js web + Expo mobile + `@orbit/shared`). When you change an endpoint here, the consumer side MUST update in lockstep:

| API change | Consumer change |
|---|---|
| New endpoint | `packages/shared/src/api/endpoints.ts` constant + Server Action (web) + apiClient call (mobile) + query key (if read) |
| New DTO field | `packages/shared/src/types/*.ts` Zod schema |
| Renamed/removed field | Both Zod schema AND every callsite in both apps |

If you're launched from `orbit-ui-mobile`, do the consumer edits in the same session.

## LSP

C# LSP (Roslyn, via the CWM.RoslynNavigator MCP server — install once with `dotnet tool install -g CWM.RoslynNavigator`) is wired into Claude Code through the **mobile repo's** `.mcp.json` (sessions are almost always launched from there), pointed at this repo's `Orbit.slnx`. It exposes symbol-level tools (`find_symbol`, `find_references`, `find_implementations`, `get_type_hierarchy`, `get_diagnostics`, ...) instead of reading whole files. When launched directly from orbit-api, no LSP — fall back to grep/glob/read.

## Git workflow

Branch protection on `main`: no direct pushes, no force pushes, no branch deletion. Branches: `feature/xxx`, `fix/xxx`, `chore/xxx`. **Squash merge only.** **`main` requires zero approving reviews, and `pullfrog-approval` is a required status check pinned to app id `1768019`**: the blocking path is the required contexts, which hold the deterministic `guards.yml` jobs plus `pullfrog-approval`. Pullfrog is the only pull request reviewer. Its review instructions live in the Pullfrog console, never in a repository file, because a pull request can edit any file on its own head. Never reuse a branch after its PR is squash-merged. Never `--no-verify`/`--no-gpg-sign`. **Never perform an admin merge in any shape**: no `gh pr merge --admin`, no direct `PUT /repos/{owner}/{repo}/pulls/{number}/merge`, and no direct GraphQL `mergePullRequest` mutation. Naming the two raw API calls is deliberate; forbidding only the CLI flag leaves both API paths open. The admin override belongs to the owner alone: if a merge genuinely needs one, STOP and ask the owner to merge it.

Pre-commit hook: a git-level `pre-commit` (lefthook, `lefthook.yml`) runs `dotnet format Orbit.slnx --include <staged .cs>` on staged `.cs` files and re-stages the fixes. This repo has no Node, so there is no auto-install — bootstrap once per machine with `winget install evilmartians.lefthook` (fallbacks: `scoop install lefthook`, or a GitHub-release binary + `lefthook self-update`), then run `lefthook install` inside the clone to write `.git/hooks/pre-commit`.

## Testing

- xUnit + FluentAssertions. Unit tests only — the integration suite was removed as outdated; do not add integration tests or a real-DB harness.
- Every new feature needs unit tests: commands, queries, validators, domain logic.
- Test accounts bypass email verification via the `TEST_ACCOUNTS` env var (`email:code` pairs) — see `tests/CLAUDE.md`.
- **Query-shape / N+1 guard:** `tests/Orbit.Infrastructure.Tests/Persistence/QueryRoundTripCountTests.cs` runs the filtered-include and batched reads (`GetRetrospectiveQuery`, `GetDailySummaryQuery`, `ExportUserDataQuery` shapes) over SQLite in-memory with a `CountingDbCommandInterceptor` and asserts the SQL round-trip count is invariant to row volume — the tripwire for an N+1 regression. Reuses the shared `SqliteOrbitDbContextFactory` shim (extracted for these tests; the ~6 inline copies remain a dedup follow-up).
- **Benchmarks (not tests):** `bench/Orbit.Benchmarks` is a BenchmarkDotNet console (`net10.0`, `[MemoryDiagnoser][ShortRunJob]`) over the three pure hot paths (`HabitScheduleService`, `HabitMetricsCalculator.Calculate`, `RetrospectiveMetricsCalculator.Compute`). It is deliberately outside `dotnet test` and coverage, and no CI job runs it. Run locally: `dotnet run -c Release --project bench/Orbit.Benchmarks -- --filter '*'` (or `--list flat` to enumerate).

## Deployment

Render (Docker, manual API releases through `release.yml`) + Supabase PostgreSQL (session pooler) + Amazon SES (email) + Firebase (FCM) + Stripe.

Release admission requires completed successful `API Tests` and `Guards` runs from
`push` or `workflow_dispatch` on `main` at exactly the selected SHA, plus successful
check runs. For a bot-merged commit without push CI, confirm `main` is still at that
commit, run `gh workflow run test.yml --ref main --repo thomasluizon/orbit-api` and
`gh workflow run guards.yml --ref main --repo thomasluizon/orbit-api`, verify both
runs succeed with that `head_sha`, then rerun Release API. Runs for another SHA or
branch, including pull request runs, do not qualify. See
[API release admission](infra/README.md#api-release-admission) for recovery details.
