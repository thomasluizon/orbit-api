# AGENTS.md (orbit-api)

Instructions for Codex CLI workers. Claude Code reads CLAUDE.md; this file holds the
worker contract and DEFERS to `CLAUDE.md` (same directory) for repo conventions. Read
CLAUDE.md before writing code.

Pullfrog reviews every pull request from GitHub Actions. Its review instructions live in
the Pullfrog console, never in a repository file, because a pull request can edit any
file on its own head. Nothing here configures that review.

## Worker contract

- Your prompt is a GitHub ticket body. Execute exactly it; an impossible or
  contradictory ticket means STOP and report, never improvise.
- Finish = `dotnet build Orbit.slnx` 0 errors + `dotnet test` green, commit, push, one
  PR to `main` linking the ticket reference (`ORB-N` or `#N`), then stop. Never merge.
- Post the approach before you write the code. Open the pull request as your FIRST act
  after creating the branch, carrying no implementation yet, and immediately post your
  intended approach as a pull request comment: the change you mean to make, the files it
  will land in, and why that shape rather than the alternatives you rejected. Only then
  start writing. A wrong shape then costs one comment instead of a review round against
  code you already wrote. Changing a plan is free; changing a merged design is not.
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

### Guardrails you must not trip

These hold for EVERY worker and every engine. They are enforced by CI, GitHub branch
protection, and the lefthook pre-commit/pre-push hooks, NOT by the Claude Code session
hooks (those do not run under `codex exec` or a raw shell). This list is the readable
copy; the gates are the enforcement.

- Never push or force-push to `main`/`master`. Branch to `feature/`|`fix/`|`chore/`,
  open a PR, squash-merge only. Never reuse a squash-merged branch.
- Never perform an admin merge, in any shape: no `gh pr merge --admin`, no direct
  `PUT /repos/{owner}/{repo}/pulls/{number}/merge`, and no GraphQL `mergePullRequest`
  mutation. Naming the two raw API calls is deliberate; forbidding only the CLI flag
  leaves both API paths open. The admin override exists for Thomas alone. If a merge
  genuinely needs it, STOP and ask Thomas to merge it himself.
- Never bypass the git hooks: no `--no-verify` (or its `-n` commit alias), no
  `--no-gpg-sign` and no `commit.gpgsign=false`. Fix what a hook flags, then commit.
- Never `git worktree remove --force`: on Windows it follows a junction and deletes the
  link target. Remove the junctions first, then remove the worktree without `--force`.
- EF migrations must be idempotent: raw `CREATE INDEX` inside `migrationBuilder.Sql(...)`
  needs `IF NOT EXISTS`, and raw `DROP INDEX` needs `IF EXISTS` (EF re-applies at startup
  on Render; a duplicate raw `CREATE INDEX` throws Postgres 42P07 and fails the deploy).
  The fluent `migrationBuilder.CreateIndex(...)`/`DropIndex(...)` API is already safe.
  The Guard Migrations CI job enforces this over changed `Migrations/*.cs` files.

### Never assume an external interface. Check it, then use it.

Before you read a field, flag, subcommand, exit code, or response shape from anything
outside this repository, confirm it exists. External means a CLI (`orca`, `gh`, `git`,
`codex`, `dotnet`), an HTTP API, or a NuGet package you did not write.

Confirm it against the response itself, in this order:

1. Run a real invocation and read what came back.
2. If the command writes, read the installed package's own source where it builds the
   response.
3. If neither is possible, use schema introspection.

`--help` proves that a flag or a subcommand exists and nothing whatever about a response
body, so it never confirms a field. Documentation, your memory of a similar tool, and what
the shape "should" obviously be are never authority.

**Never satisfy an assumption by writing the fixture that agrees with it.** You author both
the code and its test double, so a fixture built from a guess makes the suite prove that
your code matches your belief, not that it works. A green test over an invented field is
worth less than no test at all, because it buys false confidence.

**If you cannot confirm the field, do not read it.** Redesign so the unknown is not on the
path: use a value the interface already returns and the codebase already reads, or make the
operation's success depend only on the exit code. Say in the pull request body which field
you wanted and why you could not confirm it. Treating an unconfirmed field as a failure
signal is not failing closed, it is inventing a failure, because "the call failed" and "the
field does not exist" arrive as the same value.

When your change reads an external field, put the evidence in the pull request body,
whether or not the codebase already reads that field elsewhere. An existing read is not
evidence: it may be the unverified guess this rule exists to catch. You may cite the pull
request that first proved the field instead of running the command again.

## Defects no gate catches

Check these five before you open the pull request. CI owns the mechanical defects. These
five are P0/P1, and no gate sees them.

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
5. **A field, flag, exit code, or response shape read from an external interface with no
   evidence in the pull request body, or a test fixture asserting a shape no evidence
   supports.** No CI job can see this: the suite is green precisely because the author
   wrote both the code and the fixture. Safe path: the complete redacted response shape
   plus a way to re-derive it, or a design that does not read the unconfirmed field.

### Minimum supported version operations

The live floor is the `Value` of the `MinSupportedVersion` row in the production
`AppConfigs` table. Read it before planning a raise and read it back after every write:

```sql
SELECT "Key", "Value"
FROM "AppConfigs"
WHERE "Key" = 'MinSupportedVersion';
```

The initial raise to `1.3.11` is versioned in the EF migration that introduced that
value. Future raises are policy changes and use a guarded production database update
after the preconditions below are recorded in the owning ticket:

```sql
UPDATE "AppConfigs"
SET "Value" = '<new floor>'
WHERE "Key" = 'MinSupportedVersion'
  AND "Value" = '<current floor>';
```

- Read the active version distribution and release dates from Google Play Console.
- Confirm the proposed floor is already live and sends `X-App-Version` and handles the
  existing HTTP 426 response with a usable upgrade prompt.
- Check `APP_VERSION` in every deployed environment of the Orbit web project in Vercel.
  An unset value is fail open. A set value must be at least the proposed floor before
  changing the database row.
- Read the row back and verify its exact value. Monitor Android and web HTTP 426 traffic
  and client reports for 24 hours.

`AppConfigService` caches the value for 30 minutes in each process. A change takes up
to 30 minutes to affect a warm process, and running instances can start enforcing it
at different times. Do not treat the database write as immediate fleet-wide activation.

Rollback does not require a deploy. Set the row to `0.0.0`, read it back, and allow the
same per-process cache window for recovery:

```sql
UPDATE "AppConfigs"
SET "Value" = '0.0.0'
WHERE "Key" = 'MinSupportedVersion';
```
