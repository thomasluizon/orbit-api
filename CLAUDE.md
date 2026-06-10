# Orbit API

.NET 10 / EF Core / MediatR CQRS REST API. Hosts at `api.useorbit.org`.

## Stack

.NET 10, C# 13, PostgreSQL via EF Core 10, MediatR 14, FluentValidation 12, JWT Bearer, OpenAI GPT-4.1-mini, BCrypt, Firebase Admin SDK (FCM), Lib.Net.Http.WebPush (VAPID).

## Architecture (one-pager)

Clean architecture, four projects: `Orbit.Api` → `Orbit.Application` → `Orbit.Domain` ← `Orbit.Infrastructure`. CQRS via MediatR. Commands and queries live per-feature folder in `Orbit.Application`. Domain entities use factory methods (`Habit.Create()`, `User.Create()`). Generic repository + Unit of Work in Infrastructure. Each project has a scoped `CLAUDE.md` — read those when editing inside that project.

## Cross-cutting hard rules

These apply everywhere — they override project-local conventions if they conflict.

- **Timezone:** All user-facing dates MUST use `IUserDateService.GetUserTodayAsync(userId)`. NEVER use `DateOnly.FromDateTime(DateTime.UtcNow)` for user-facing logic. `DateTime.UtcNow` is only acceptable for `CreatedAtUtc` timestamps in entity factories and cache key generation.
- **Authorization:** Every controller endpoint requires JWT Bearer unless it's `/health` or `/api/auth/*`. If you add an endpoint, default to `[Authorize]`. Use `[AllowAnonymous]` explicitly only when the endpoint truly is public.
- **Validation:** Every new feature needs validators in `Orbit.Application/<Feature>/Validators/` AND domain-entity guards in factory/update methods. The backend is the source of truth — never trust the frontend.
- **Logging:** Inject `ILogger<T>`, log business events with structured properties in PascalCase, English only: `logger.LogInformation("Action {Property}", value)`.
- **No workarounds.** Root-cause every bug. No `TODO`/`FIXME`/`HACK` in committed code. No empty `catch {}`. No `as any`-equivalents like unjustified `null!`.
- **No dead code.** Delete unused methods, types, parameters, branches the moment they become orphaned by your change.
- **No narration comments (analyzer-enforced).** Code must read without prose. The only comments allowed are XML-doc comments (`///` or `/** */`) documenting a symbol's intent/contract, and a WHY note that links an upstream issue/PR/doc URL. Everything else is an error (`ORBIT0001`, from `src/Orbit.Analyzers`) and is autofixable via `dotnet format`. To explain code, rename it or extract a well-named method — don't narrate.

## Cross-repo parity contract

The TypeScript consumer is `thomasluizon/orbit-ui-mobile` (Turborepo: Next.js web + Expo mobile + `@orbit/shared`). When you change an endpoint here, the consumer side MUST update in lockstep:

| API change | Consumer change |
|---|---|
| New endpoint | `packages/shared/src/api/endpoints.ts` constant + Server Action (web) + apiClient call (mobile) + query key (if read) |
| New DTO field | `packages/shared/src/types/*.ts` Zod schema |
| Renamed/removed field | Both Zod schema AND every callsite in both apps |

If you're launched from `orbit-ui-mobile`, do the consumer edits in the same session. The mobile harness has a `contract-aligner` subagent — invoke it when both repos have staged changes.

## LSP

C# LSP (OmniSharp/Roslyn) is wired into Claude Code via the **mobile repo's** `.mcp.json` (sessions are almost always launched from there). When launched directly from orbit-api, no LSP — fall back to grep/glob/read.

## Git workflow

Branch protection on `main`: no direct pushes, no force pushes, no branch deletion. Branches: `feature/xxx`, `fix/xxx`, `chore/xxx`. **Squash merge only.** Never reuse a branch after its PR is squash-merged.

## Testing

- xUnit + FluentAssertions. Unit tests only — the integration suite was removed as outdated; do not add integration tests or a real-DB harness.
- Every new feature needs unit tests: commands, queries, validators, domain logic.
- Test accounts bypass email verification via `REVIEWER_TEST_EMAIL` / `QA_TEST_CODE` env vars — see `tests/CLAUDE.md`.

## Deployment

Render (Docker, auto-deploy on push to main) + Supabase PostgreSQL (session pooler) + Resend (email) + Firebase (FCM) + Stripe.
