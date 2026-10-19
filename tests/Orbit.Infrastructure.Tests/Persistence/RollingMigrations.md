# Rolling schema migrations

`RollingMigrationGate.cs` is enforced by the existing **Guard Migrations** CI job.
It evaluates compiled PostgreSQL `UpOperations` for migrations the PR adds
or changes relative to its base, plus every recorded deployment contract.
Designer changes select the same migration;
snapshots do not select historical migrations. The existing append-only gate
also rejects edits to applied migrations. Never repair applied history.

Run the gate locally after committing the migration:

```sh
ORBIT_MIGRATION_GUARD_BASE=origin/main dotnet test tests/Orbit.Infrastructure.Tests --filter FullyQualifiedName~RollingMigrationGateTests
```

Without that variable, the unit cases still run, but the repository ancestry case is
inactive. Guard Migrations sets it explicitly and checks every recorded contract
against real Git ancestry using its full-history checkout. A missing base or
unrecognized migration fails the repository case instead of silently skipping inspection.

## Expand, deploy, contract

1. **Expand.** Add the replacement column or table without dropping or renaming
   the old one. Existing writers must still work: use nullable additions, safe
   defaults, or a compatibility trigger where needed. Backfill existing rows and
   preserve writes made by overlapping old processes. Keep old readers working.
2. **Deploy the compatible binary.** Ship code that reads and writes the expanded
   schema and no longer needs the object that will be retired. During overlap,
   both binaries must work against the expanded schema. For a replacement,
   migrate readers and writers separately if needed, with dual writes or a
   trigger until no old writer remains. Test old and new read/write paths against
   the expanded schema, including backfill and concurrent-write behavior.
3. **Record the deployment barrier.** Before adding the contract migration,
   confirm the compatible commit is live on every production web instance and
   every worker. Record Render service, deploy and commit identifiers, evidence
   that previous instances are deactivated, and completion or cancellation of
   their in-flight work. This includes hosted background services, reminder
   scans, Hangfire servers, executing jobs, retries and queued jobs whose payloads
   or handlers still require the old schema. If any worker is separate from the
   web service, verify it separately. A healthy new web instance alone is not a
   barrier. Waiting a fixed time is not evidence.
4. **Contract in a later PR and release.** Add a new migration and an entry keyed
   by its exact migration ID in `.github/migration-contracts.json`. Both commits
   below must already be ancestors of the PR base, so a same-PR expand and
   contract cannot pass. Record the safe rollback floor as the compatible
   deployed commit in the release evidence. After contraction, never roll back
   to a binary that still needs the retired schema. Use a forward repair or
   an explicitly reviewed schema restoration when necessary.

Each contract entry has these required fields:

| Field | Evidence |
| --- | --- |
| `expandCommit` | Full commit hash that introduced the expanded schema or compatibility code |
| `deployedCommit` | Full compatible commit hash confirmed live before contraction; must contain the expand commit |
| `evidenceUrl` | HTTPS link to reviewed deployment evidence in the owning PR or ticket |
| `condition` | The specific read/write or backfill condition confirmed before removing compatibility |
| `webDrainEvidence` | Services, live deploys and commits, plus deactivation and drain of incompatible web instances |
| `backgroundDrainEvidence` | Drain of incompatible hosted services, Hangfire workers and in-flight or queued work, including any separate workers |

The gate checks required evidence fields, commit membership in the base and
expand-to-deployed ancestry; review must verify the operational claims.
The allowlist is an attestation, not an automatic deployment lookup. A barrier
for one migration does not authorize any other migration.

The entry for `20261019011031_ContractHabitLogSlip` records the production barrier
attested in [API PR 764](https://github.com/thomasluizon/orbit-api/pull/764#issuecomment-6257602012).
Its unit regression runs the actual migration without evidence, then validates the
committed entry's shape using injected ancestry, independently of checkout depth.
The repository case checks that same contract with real Git ancestry in Guard
Migrations, even when its migration is unchanged. No applied migration is modified.

Column and table drops/renames, schema drops, column alterations and required
columns without defaults need a barrier. Raw SQL containing drops, renames,
truncation, column/type alterations, dynamic execution or replacement definitions
also needs one, including compatibility trigger/function removal. SQL matching
scans the original SQL, including comments and literals, so a quoted comment
marker cannot hide later DDL. It is deliberately conservative and may require
review for harmless text; it is not a PostgreSQL parser. Review SQL function
calls and any indirect DDL as well.
Additive tables, nullable/defaulted columns, indexes and ordinary backfills pass.
Raw `ALTER TABLE` actions are split at commas outside quoted text and parentheses.
Every action must be an `ADD COLUMN` using a recognized built-in type, nullable
or supplied with a default, without added constraints. Unclassified actions and
types require a barrier, including trigger state changes, schema moves and
`ADD CONSTRAINT`, even with `NOT VALID`.
`Down` is not executed by forward deployments and does not trigger this gate.

Startup awaits `Database.MigrateAsync` before the new host serves requests or
starts hosted jobs. That does not stop other web or Hangfire processes that are
already running against the shared database. The deployment barrier is required
even when startup migration ordering is correct.

## Incident regression

`20260823203026_DailyAiQuotaByLocalDate` is read-only incident evidence. It drops
`Users.AiMessagesResetAt` and renames `AiMessagesUsedThisMonth` in the same release
that replaces them in the model. `HistoricalQuotaMigration_WithoutBarrier_IsRejected`
runs a fresh instance of that unchanged migration through the gate and rejects
both operations. The paired barrier case models a later, reviewed contraction;
it does not attest that the historical deployment had a barrier.

[ORBIT-API-11](https://thomasluizon.sentry.io/issues/7688449367/) recorded one
handled PostgreSQL 42703 event in `ProcessRelativeReminders` about three minutes
after [API PR 492](https://github.com/thomasluizon/orbit-api/pull/492) merged.
Rolling overlap is the leading hypothesis,
not a confirmed process timeline. Event detail was access-restricted and the
Render rejected an incident-window log query because its start was beyond the
30-day retention window; no process identity was established.
This guard changes no production schema or quota values and does not suppress
the issue.

## Rollout and observation

Merge the CI guard through its PR. No production DDL, environment variable or
database action is required. Reverting the guard commit disables it. Count
destructive migrations blocked in Guard Migrations and inspect their diagnostics.
For each subsequent schema rollout, watch production Sentry for PostgreSQL
42703 missing-column errors, especially background reminder scans, and record
the result in the rollout ticket. Absence of recurrence requires a real later
rollout; unit tests cannot establish it.
