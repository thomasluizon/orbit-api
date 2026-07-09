# Orbit API

.NET 10 / EF Core / MediatR CQRS REST API. Hosts at `api.useorbit.org`.

## Stack

.NET 10, C# 13, PostgreSQL via EF Core 10, MediatR 14, FluentValidation 12, JWT Bearer, OpenAI GPT-4.1-mini, BCrypt, Firebase Admin SDK (FCM), Lib.Net.Http.WebPush (VAPID).

## Architecture (one-pager)

Clean architecture, four projects: `Orbit.Api` → `Orbit.Application` → `Orbit.Domain` ← `Orbit.Infrastructure`. CQRS via MediatR. Commands and queries live per-feature folder in `Orbit.Application`. Domain entities use factory methods (`Habit.Create()`, `User.Create()`). Generic repository + Unit of Work in Infrastructure. Each project has a scoped `CLAUDE.md` — read those when editing inside that project.

## Cross-cutting hard rules

These apply everywhere — they override project-local conventions if they conflict.

- **Timezone:** All user-facing dates MUST use `IUserDateService.GetUserTodayAsync(userId)`. `DateTime.UtcNow` is only for `CreatedAtUtc`/`UpdatedAtUtc` timestamps and cache keys — the `csharp-tz` hook blocks raw `DateTime.UtcNow`/`DateOnly.FromDateTime(DateTime.UtcNow)` in `Orbit.Application`.
- **Authorization:** default `[Authorize]`; the `csharp-authz` hook blocks a Controller with no `[Authorize]`/`[AllowAnonymous]`. Exempt (mark `[AllowAnonymous]`): `/health`, `/api/auth/*`, signature-verified webhooks.
- **Validation:** Every new feature needs validators in `Orbit.Application/<Feature>/Validators/` AND domain-entity guards in factory/update methods. The backend is the source of truth — never trust the frontend.
- **Logging levels.** Inject `ILogger<T>`; structured properties in PascalCase, English only (`logger.LogInformation("Action {Property}", value)`). Reserve each level so prod logs carry only signal:
  - **Trace/Debug** — routine per-operation success, AI call lifecycle + token usage, per-step progress, per-delivery sends. Filtered out in prod.
  - **Information** — low-frequency business/lifecycle events only: signup, subscription change, sync-completed-with-N (gate the log on `N > 0`), account deletion, streak-freeze activation, the daily AI-cost summary, service start/stop. Never internal steps.
  - **Warning** — recoverable/degraded paths (retries, empty AI response, best-effort metric-write failure).
  - **Error** — actionable failures. Let Sentry capture exceptions; don't add a redundant manual `LogError(ex)` that mirrors a captured exception — keep exactly one Sentry issue + one console error per unhandled exception (the `UnhandledExceptionHandler` console log is held off Sentry via its `Logging:Sentry:LogLevel` category).
  - Prod minimum levels (framework namespaces → `Warning`) live in `appsettings.Production.json`.
- **No workarounds.** Root-cause every bug. No `TODO`/`FIXME`/`HACK` in committed code. No empty `catch {}`. No `as any`-equivalents like unjustified `null!`.
- **No dead code.** Delete unused methods, types, parameters, branches the moment they become orphaned by your change.
- **Narration comments are `ORBIT0001` build errors** (`src/Orbit.Analyzers`; only `///`/`/** */` XML-doc or a URL-linked WHY note survive; autofix via `dotnet format`). The analyzer is silent in local builds but fails CI — grep for bare `//` before pushing.
- **Redundant transaction rollbacks are `ORBIT0002` build errors** (`src/Orbit.Analyzers`, category Reliability): an explicit `RollbackAsync()`/`Rollback()` on a `using`/`await using`-scoped EF `IDbContextTransaction` is banned — scope disposal already rolls back an uncommitted transaction, so let the using-scope dispose it and keep the `catch` to `ChangeTracker.Clear(); throw;`. A genuinely manually-owned transaction (declared without `using`, or reached via a field/parameter) is left alone. Like ORBIT0001 it is silent in local builds but fails CI (the in-box SDK compiler is older than the analyzer's `Microsoft.CodeAnalysis` 5.6.0, so csc skips it with `CS9057` locally).

## Cross-repo parity contract

The TypeScript consumer is `thomasluizon/orbit-ui-mobile` (Turborepo: Next.js web + Expo mobile + `@orbit/shared`). When you change an endpoint here, the consumer side MUST update in lockstep:

| API change | Consumer change |
|---|---|
| New endpoint | `packages/shared/src/api/endpoints.ts` constant + Server Action (web) + apiClient call (mobile) + query key (if read) |
| New DTO field | `packages/shared/src/types/*.ts` Zod schema |
| Renamed/removed field | Both Zod schema AND every callsite in both apps |

If you're launched from `orbit-ui-mobile`, do the consumer edits in the same session. The mobile harness has a `contract-aligner` subagent — invoke it when both repos have staged changes.

## LSP

C# LSP (Roslyn, via the CWM.RoslynNavigator MCP server — install once with `dotnet tool install -g CWM.RoslynNavigator`) is wired into Claude Code through the **mobile repo's** `.mcp.json` (sessions are almost always launched from there), pointed at this repo's `Orbit.slnx`. It exposes symbol-level tools (`find_symbol`, `find_references`, `find_implementations`, `get_type_hierarchy`, `get_diagnostics`, ...) instead of reading whole files. When launched directly from orbit-api, no LSP — fall back to grep/glob/read.

## opencode compatibility

The workflow is tool-agnostic between Claude Code and opencode; `.claude/` stays the single source of truth.

- opencode reads this `CLAUDE.md` natively (fallback when no `AGENTS.md` exists) and discovers `.claude/skills/**` as-is. **Never create an `AGENTS.md`** — it would shadow this file and fork the rules.
- `opencode.json` (committed) loads the scoped `tests/CLAUDE.md`.
- `.opencode/agents/*.md` are thin pointers to the matching `.claude/agents/*.md` bodies. Edit behavior in `.claude/agents/` only; when adding a new agent, create both the body and its pointer.

## Git workflow

Branch protection on `main`: no direct pushes, no force pushes, no branch deletion. Branches: `feature/xxx`, `fix/xxx`, `chore/xxx`. **Squash merge only.** Never reuse a branch after its PR is squash-merged.

## Testing

- xUnit + FluentAssertions. Unit tests only — the integration suite was removed as outdated; do not add integration tests or a real-DB harness.
- Every new feature needs unit tests: commands, queries, validators, domain logic.
- Test accounts bypass email verification via the `TEST_ACCOUNTS` env var (`email:code` pairs) — see `tests/CLAUDE.md`.

## Deployment

Render (Docker, auto-deploy on push to main) + Supabase PostgreSQL (session pooler) + Resend (email) + Firebase (FCM) + Stripe.
