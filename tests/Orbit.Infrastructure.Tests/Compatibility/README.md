# API redesign compatibility

The baseline is `main` at `8356d4debec5b48105734eb7d5dfd28559332da8`.
The promoted tree is `redesign/main` at `0a32fab3e366d0029c8ea3d478841215ee1d8b88`.
The delivery branch adds the calendar error compatibility fix and these proofs.

## OpenAPI comparison

The committed documents contain 194 baseline operations and 201 promoted operations.
`oasdiff` v1.33.0 is the version used by the repository's pinned breaking-change action.
Run from the repository root with its `.oasdiff.yaml` and severity overrides:

```sh
oasdiff breaking 8356d4de:src/Orbit.Api/openapi.json src/Orbit.Api/openapi.json --fail-on ERR
oasdiff changelog 8356d4de:src/Orbit.Api/openapi.json src/Orbit.Api/openapi.json --format markdown
```

The breaking check reports no breaking changes. All baseline routes, properties,
types, enum values, required-property sets, parameters and response statuses survive.
No client usage exemption is needed.

| Contract | Change | Compatibility |
| --- | --- | --- |
| Routes, fields, enums, required parameters | No removals, renames or retypes; no new requirement on an existing operation | Preserved |
| `GET /api/Calendar/events` | Optional `includeImported=false`; declares the existing event array as its 200 response | Default retains the existing filtering and serialized fields; imported markers appear only with opt-in |
| `SkipHabitRequest` | Optional nullable `skipId` | Old bodies and the existing 204 response remain valid |
| `ChatResponse` | Optional `periodInsight`, `daySummary`, `streak`, `calendar`, `recordLists`, `accountRows`, `followUps` | Existing response fields remain |
| `GoalListCardItem` | Optional `trackingStatus`, `progressPercentage`, `projectedCompletionDate`, `daysToDeadline` | Existing item fields remain |
| `MetricsCard` | Optional `series`, `topHabitName`, `topHabitEmoji`, `habitId`, `habitTitle`, `weeklyCompletionRate`, `monthlyCompletionRate`, `lastCompletedDate` | Existing metric fields remain |
| `PendingAgentOperation` | Optional `changes`, `changeTargetCount`, `items`, `previewFingerprint`, `actionKey` | Existing approval fields remain |
| `ProfileResponse` | Optional `activeApiKeyCount` | Existing profile fields remain |
| New operations | Chat record pages; skip undo; clock format; pending preview refresh and revision; push subscription list; OAuth assets | Seven additive operations |

Source inspection also covers responses whose controllers do not declare their payload
types in OpenAPI:

| Response | Change | Compatibility |
| --- | --- | --- |
| Notifications | Optional `totalCount` | Existing `items` and `unreadCount`, default limit and offset remain |
| Habit metrics | Optional `habitId`, `habitTitle` | Existing metric values remain |
| Retrospective | Optional `dateFrom`, `dateTo`, `metrics.completionSeries`, and `habitId` on habit statistics | Existing metrics, narrative and cache fields remain |
| Profile export settings | Nullable `uses24HourClock` | Existing settings, including the stored historical color scheme, remain |
| Validation failure | Additional `errorDetails` with codes and messages | Existing `type`, `status`, `requestId`, `errors: string[]` remain |
| Chat SSE | Additional `step` variant and optional `domain`, `access` fields | Steps require `supportsToolSteps=true`; existing event variants remain |
| Chat client context | Optional capability fields for previews, steps, follow-ups and cards; optional `messageOrigin` | Missing capabilities keep legacy behavior |
| Calendar connection errors | Portuguese localization breaks a shipped English substring classifier | Keep these two messages in English for both languages, preserving their codes and statuses |
| Profile color scheme | Historical writes remain accepted; profile reads return the existing `orange` key | The shipped profile schema accepts nullable strings; no enum value is introduced or removed |

`AgentContextSnapshot.ColorScheme` was removed from the internal prompt context only.
`BuildAgentContextSnapshot` and `BuildDynamicSupplement` are its only production
producer and consumer; no controller or MCP tool returns this record.

## Shipped client evidence

Both production web and Android 1.3.57 (116) use client commit
`b84bdc9ed91d57b147c7a8f76980c89cfe7ebdae`. GitHub deployment `6803094988`
records `task=android-release`, that exact SHA, `app_version=1.3.57`,
`android_version_code=116`, `track=open` and `api_base=https://api.useorbit.org`.
Re-read it with `gh api repos/thomasluizon/orbit-ui-mobile/deployments/6803094988`.

Relevant source at that commit:

- [Shared calendar classifier](https://github.com/thomasluizon/orbit-ui-mobile/blob/b84bdc9e/packages/shared/src/utils/calendar-sync.ts#L173).
- [Web calendar hook](https://github.com/thomasluizon/orbit-ui-mobile/blob/b84bdc9e/apps/web/hooks/use-calendar-events.ts#L22).
- [Android calendar hook](https://github.com/thomasluizon/orbit-ui-mobile/blob/b84bdc9e/apps/mobile/hooks/use-calendar-events.ts#L18).
- [Calendar array schema](https://github.com/thomasluizon/orbit-ui-mobile/blob/b84bdc9e/packages/shared/src/types/calendar.ts#L41).
- [Chat response and stream schemas](https://github.com/thomasluizon/orbit-ui-mobile/blob/b84bdc9e/packages/shared/src/types/chat.ts#L176).
- [Profile schema](https://github.com/thomasluizon/orbit-ui-mobile/blob/b84bdc9e/packages/shared/src/types/profile.ts#L50).

## Schema and legacy binary proof

`RedesignMigrationCompatibilityTests` compares the actual PostgreSQL relational
model with `AddMarketingContacts.TargetModel`, the last migration model on the
baseline. Only column, table and nonunique-index additions are allowed. It also
checks the real migration operations and PostgreSQL idempotent history guards.
No database connection is opened for these checks.

| Missing migration | Effect on an existing main database |
| --- | --- |
| `AddPendingOperationRevision` | Nullable `PreviewFingerprint`; `RevisionNumber NOT NULL DEFAULT 0`, so old inserts can omit both |
| `LinkGamificationNotificationsToProgress` | Adds an archive table, saves original titles and bodies, then updates recognized notifications in batches of 1000 to the existing `/progress` destination; no existing schema is removed |
| `AddClockFormatPreference` | Nullable `Users.Uses24HourClockPreference`, so old inserts and rows need no value |
| `AddHabitSkipUndo` | Adds a table and two nonunique indexes; its habit foreign key cascades deletion, so old habit deletion remains valid |

`AddPendingOperationRevision` sorts before the last baseline migration. EF applies
missing IDs from migration history; a script generated only from the last baseline
ID would omit it. The idempotent full script contains a history guard for each of
the four missing IDs. Existing migrations and `MinSupportedVersion` are unchanged.

`MainSchemaProbe.cs.txt` runs against an archive of the actual baseline source,
including its original domain entities and `OrbitDbContext`. Its SQL was emitted
by EF's SQLite migration generator from the three structural migrations' real
`UpOperations`. It proves legacy reads, inserts and updates, omitted-column defaults,
and habit deletion with a populated undo table. It uses SQLite in memory and starts
no server. The PostgreSQL notification data rewrite is inspected separately and is
not executed by this SQLite proof.

Reproduce from the delivery checkout:

```sh
api_compat_archive=$(mktemp -d)
git archive 8356d4de | tar -x -C "$api_compat_archive"
cp tests/Orbit.Infrastructure.Tests/Compatibility/MainSchemaProbe.cs.txt "$api_compat_archive/tests/Orbit.Infrastructure.Tests/Persistence/RedesignSchemaProbeTests.cs"
dotnet test "$api_compat_archive/tests/Orbit.Infrastructure.Tests" --filter FullyQualifiedName~RedesignSchemaProbeTests
dotnet test tests/Orbit.Infrastructure.Tests --filter FullyQualifiedName~RedesignMigrationCompatibilityTests
```

## Release and sync

Production release remains a manual dispatch of `.github/workflows/release.yml`
from `main`, with `environment=production` and `branch=main`, after squash merge.
The workflow must verify `/health` against the merged commit before reporting success.
This pull request does not deploy or raise the supported client floor.

After merge, record in `thomasluizon/orbit-tickets#746` that the redesign payload
already exists on `redesign/main`, so the sync must not replay it. Only the delivery
branch's calendar compatibility fix and proof artifacts are new to that branch.
