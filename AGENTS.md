# AGENTS.md (orbit-api)

Instructions for Codex (CLI workers and the cloud reviewer). Claude Code reads
CLAUDE.md; this file holds the worker contract and the review rules and DEFERS to
`CLAUDE.md` (same directory) for repo conventions. Read CLAUDE.md before writing code.

## Worker contract

- Your prompt is a Linear ticket body. Execute exactly it; an impossible or
  contradictory ticket means STOP and report, never improvise.
- Finish = `dotnet build Orbit.slnx` 0 errors + `dotnet test` green, commit, push, one
  PR to `main` linking `ORB-N`, then stop. Never merge.
- Analyzer gates (silent in local builds, CI-fatal): ORBIT0001 narration comments,
  ORBIT0002 redundant rollbacks, ORBIT0003 controller authorization, ORBIT0004 raw
  `DateTime.UtcNow` for user-facing dates (use `IUserDateService.GetUserTodayAsync`),
  ORBIT0005 unconfigured DbSet. Grep for bare `//` comments before pushing.
- The dash ban is CI-enforced (`tools/check-dashes.mjs`): never type an em dash
  anywhere, including commits and PR text.
- New features need FluentValidation validators AND domain-entity guards; the backend
  is the source of truth.
- DTO/contract changes are append-only and deploy-API-first; the TypeScript consumer
  (orbit-ui-mobile) updates in lockstep via its own ticket that this one blocks.

## Code Review Rules

Only what no gate can check; mechanical findings belong to CI. Flag P0/P1 only.

1. **A DTO field renamed, removed, or retyped that a shipped mobile client still
   reads.** No CI job knows the Play-fleet lag. Safe path: append-only optional
   fields; breaking changes use expand-contract plus `AppConfig.MinSupportedVersion`.
2. **`MinSupportedVersion` raised before the carrying build is live in the fleet.**
3. **A user-facing date computed from a UTC instant without the user's timezone**
   in a NEW pattern ORBIT0004's exemptions happen to admit (an `*AtUtc` name carrying
   display data, a "cache key" that reaches a user). The analyzer checks names, not
   intent.
4. **A background job or notification that assumes server-local "today"** (schedule
   windows, streak cutoffs): correctness depends on per-user timezones and no test
   asserts the boundary hour.
