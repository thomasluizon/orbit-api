# Chat AI Tools

This directory holds the chat agent's tool layer. Each tool is a small adapter that
exposes one capability to the AI through a JSON-schema'd contract and delegates to the
existing application logic (usually a MediatR command/query).

## The `IAiTool` contract

A tool implements `IAiTool` (`IAiTool.cs`):

- `string Name` — the stable tool name the model calls. It is also the agent
  operation id, so it must be unique across all tools.
- `string Description` — what the tool does and when to use it.
- `bool IsReadOnly` (default `false`) — read-only tools never mutate state; this drives
  the `IsAgentExecutable`/audit semantics in the catalog.
- `int Order` (default `int.MaxValue`) — execution priority within a single tool-calling
  iteration (see below).
- `object GetParameterSchema()` — the JSON schema for the tool's arguments. Build it from
  the `JsonSchemaTypes` constants to avoid duplicated string literals.
- `Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)` —
  validate arguments at the boundary, run the work, and return a `ToolResult`
  (`Success`, `EntityId`, `EntityName`, `Error`, `Payload`).

Argument parsing helpers live in `JsonArgumentParser` (same assembly, internal).

## Tool ordering (`Order`)

When the model requests several tool calls in one iteration, they are executed in
ascending `Order` (`ProcessUserChatCommand.ProcessToolCallsAsync`). The order is data on
the tool, not a hardcoded switch:

```
calls.OrderBy(c => registry.GetTool(c.Name)?.Order ?? int.MaxValue)
```

Reserved values:

| Order | Tool             | Why                                                   |
| ----- | ---------------- | ----------------------------------------------------- |
| 0     | `create_habit`   | Parent habits must exist before sub-habits/tags.      |
| 1     | `create_sub_habit` | Sub-habits depend on their parent.                  |
| 2     | `assign_tags`    | Tags attach to a habit that must already exist.       |

Every other tool keeps the default `int.MaxValue` and runs last. `OrderBy` is stable, so
tools that share an `Order` keep their original call order.

## Catalog mapping is mandatory

Every registered `IAiTool` MUST have a matching `chatTools` entry on an agent capability
in `AgentCatalogService`. At startup `BuildOperations` enumerates all registered tools and
throws `InvalidOperationException` if any tool name is not mapped to a capability. So a new
tool is always added together with its catalog `chatTools` entry and its DI registration in
`ServiceCollectionExtensions`.

## MCP routes through the same tools

The MCP server's mutating tools (`Orbit.Api/Mcp/Tools/*`) do not call MediatR directly.
They forward to `McpExecutorBridge` → `IAgentOperationExecutor`, which resolves the backing
`IAiTool` by name and the matching catalog operation, runs the same policy evaluation
(read-only-credential denial, ownership pre-check, confirmation gating) and writes an
`AgentAuditLogs` row. Chat and MCP therefore share one implementation and one policy surface
per capability; read-only MCP tools stay on MediatR.


## Typed habit query results

`query_habits` retains its formatted `EntityName` and also returns a `HabitListCard`
payload from the same filtered schedule result. Rows preserve order, hierarchy and
queried completion status. The optional `date` is the resolved user-local query day.
The scope is `date` for a historical or future schedule, `today` for the resolved
current day, and `all` for an undated query. An undated query retains its filters;
`all` does not mean the payload contains every habit.

Habit rows append optional nullable boolean `isSkippedOnDate`. Dated queries emit
true for a skipped occurrence and false otherwise; undated queries omit it. The
enclosing `date` remains authoritative after refresh and replay. The existing `status`
enum is unchanged: skipped rows keep `none`. A day's completion or skip question uses
`query_habits` with `date` and null `is_completed`, rather than inferring skip state
from completion logs. A habit list containing a skipped row additionally requires
`clientContext.supportsSkippedHabitRows: true`. Clients declare it only when they
render `isSkippedOnDate` against the enclosing date. Omission, null or false keeps
the localized typed skip text, including on stored replay. Dated query rows keep
emitting `isSkippedOnDate` regardless of the requesting client's capabilities.

The normal page size is 50, including an all-habits request; the model supplies another
size only when requested. A 51-row result with limit 50 is partial and has a signed
next cursor; limit 100 returns complete data. Consumer preview truncation cannot
change source completeness. A complete, empty filtered read retains its `search`
in exact query metadata and adds one short localized scope explanation after its
empty block. It names the typed search term without deriving anything from the message.

The chat builder carries the successful typed result into its block. Each read tool
call gets its own query identity. A continuation result's `readDirective`, for example
`[[orbit:read:call_1]]`, refers only to that call. An unqualified legacy directive
cannot choose between differently scoped results. Existing formatted strings and
legacy card properties remain supported. Ambiguity suppresses only that binding;
independent successful reads retain their cards, and the dated facts remain readable.

A `habitList` with `scope: "date"` additionally requires
`supportsDatedHabitListCard: true`. This promises parsing, display and actions that
use the returned absolute `date`. The original `supportsHabitListCard` flag and
`partKinds: ["habitList"]` alone do not establish that support. Without the new flag,
legacy and parts responses contain dated text from the actual rows, without live
habit controls. Existing `today` and `all` cards retain their original negotiation.
Tracks thomasluizon/orbit-tickets#1616.

## Period metric definitions

`get_period_analytics` requires `metric` and either `relative_period` or explicit
`date_from` and `date_to`. Dates are
inclusive user-local days, resolved before the query is issued. `habit_id`, `search`,
`tag` and `is_bad_habit` optionally constrain the source. Existing record, date and
schedule aggregates include root habits; an explicitly selected child is measured
independently. `completedHabits` includes independent children using only their own
logs, never inheriting completion from a parent or child. A calendar-month
request uses `this_month` or `last_month`, or explicit dates for a named month. The existing retrospective enums
keep their rolling definitions; `month` still means the last 30 days.

Relative period selectors include `today`, `yesterday`, `this_week`, `last_week`,
`this_month`, `last_month` and `last_7_days`. Counted rolling windows use
`last_n_days` or `last_n_weeks` with `period_count`. Current weeks and months run
from the account's calendar boundary through its local today. Previous weeks and
months are complete. Week boundaries honor the account's week-start preference.
Comparisons use `baseline_relative_period` and optional `baseline_period_count`.
Relative selectors override model-supplied absolute dates and are removed before
issuing the query, so read metadata, paging and refresh retain the exact absolute scope.

The shared read resolver also applies these selectors to `get_retrospective`,
`get_daily_summary`, `get_habit_metrics`, `get_habit_history`, `get_habit_month`,
`get_calendar_overview` and `get_habit_ranking`. Calendar event instants are derived
from the resolved local days in the account's timezone. Ranking converts its end
to the existing exclusive-end contract and retains its server-resolved month windows.
`query_goals`, `review_goals` and `get_gamification_overview` expose current goal
and lifetime account-streak snapshots rather than selectable period totals. Their
server-captured `as_of` anchor already uses the account's local today. Period habit
streak questions use `get_period_analytics` or `get_habit_metrics`.
`query_habits` accepts the single-day selectors `today` and `yesterday`; a dated
habit list represents one day rather than a multi-day period.

| Metric | Unit | Definition |
| --- | --- | --- |
| `completedHabits` | `habits` | Distinct nondeleted positive recurring or flexible habits with at least one positive, nondeleted, nonslip log in the window. Includes ended and completed habits; excludes tasks, General items and bad habits. |
| `positiveLogRecords` | `records` | Number of nondeleted positive log records in the window, including unscheduled records. |
| `distinctLoggedDates` | `days` | Number of distinct local dates with positive records across the selected habits. |
| `metScheduledDates` | `days` | Number of scheduled habit/date pairs with a positive record on that date. |
| `scheduledDates` | `days` | Scheduled habit/date pairs, bounded by account-local creation, recurrence and end dates. General and bad habits have no positive-adherence denominator. |

`PeriodHabitMetrics` contains `window: { from, to }`, the four existing counts,
optional nullable `completedHabits`,
`completionRate` (0 through 100, null without scheduled dates), `currentStreak`,
`bestStreak` and `streakScope: "period"`. These streaks count consecutive dates
with positive activity inside the requested window. Current streak ends at the
window's last date. They are distinct from lifetime habit and account streaks.
The series uses daily buckets through 31 days, then weeks aligned to the account's
week-start preference, with clipped first and last buckets. Summed series counts
match the scheduled and met counts.

Habit logs contain completion or skip markers, with no measurement quantity or
unit. Minutes, pages, litres and similar requests use the closest supported named
count metric. Neither marker values nor their sums establish measured quantities.

`get_habit_metrics` retains every lifetime field and appends `period`,
`periodSeries` and `sourceRevision`. Its optional `date_from` and `date_to` select
the period; omission resolves to the 30 local days ending today. The query also captures
`as_of` once for the existing rolling weekly/monthly rates and current streak;
refresh retains that anchor. Its revision includes account timezone and week-start
settings as well as habit and log sources. The metrics block
retains its lifetime `totalCompletions`, `currentStreak` and `bestStreak`, while
`periodMetrics` and its series carry the explicitly scoped facts.

For "how many habits did I complete" and "quantos hábitos concluí", select
`completedHabits` with unit `habits`. For "how many times" and "quantas vezes",
select `positiveLogRecords` with unit `records`. Supported period answers contain
at most one short sentence without figures, then the block; figures belong only
in the block. MCP exposes the same `get_period_analytics` query through a typed
`query` object with camelCase fields and the same inclusive dates and metric names.

## Total and comparison blocks

Both new kinds require `supportsParts: true` and their exact kind in `partKinds`.
When `metric` is `completedHabits` and `unit` is `habits`, both kinds additionally
require optional `clientContext.supportsCompletedHabitsMetric: true`. Omission,
null or false preserves a readable server-generated text answer instead of an
unsupported block, including replayed answers. Other period metrics retain their
existing negotiation. Deploy the API before the paired consumer in
thomasluizon/orbit-tickets#1763 accepts these metric and unit values and advertises
the capability.
`AstraPeriodBlocksDisabled` suppresses them. They have no legacy directive or fleet
floor requirement. Without advertisement, tools can still ground ordinary text.
Successful focused analytics survive an unavailable AI continuation: advertised
blocks or deterministic text retain each query's exact windows, named counts and
units. A failed source query remains a failure.

`periodTotal` contains `metric`, `unit`, `value`, `metrics`, `series`, nullable
`habitId` and `read`. `metrics.window` names the exact resolved dates and the metrics
include both the met numerator and scheduled denominator.

Supplying both `baseline_from` and `baseline_to` produces `periodComparison`:
`metric`, `unit`, `current`, `baseline`, `currentValue`, `baselineValue`,
`absoluteDelta`, nullable `relativeDelta`, nullable `habitId` and `read`.
`current` and `baseline` are complete `PeriodHabitMetrics` values with their own
windows and denominators. The optional `completedHabits` count is available on
`periodTotal.metrics`, `periodComparison.current` and `periodComparison.baseline`.
A missing or null count from an older producer means unknown, not zero.
Absolute delta is current minus baseline. Relative delta
is that delta divided by baseline, expressed as a ratio rather than a percentage.
A zero baseline yields null. A decrease yields a negative delta. These calculations
have no dependency on optional AI narrative generation.

## Exact read metadata and recovery

`AgentOperationResult` appends optional `access` with `"read"` or `"write"` for
every known tool or capability, including failed and policy-denied operations.
Chat responses and final stream responses use the same field. Missing metadata
omits the field and means unknown. Clients can distinguish read failures from
write failures without maintaining a tool-name list. Deploy the API before the
consumer in thomasluizon/orbit-tickets#1808 uses this field.

All affected card types append optional `read` metadata. `AgentOperationResult`
optionally carries `readBlock: { kind, block, read }`. The metadata is:

```json
{
  "queryId": "a UUID unique to this originating read",
  "queryFamily": "the fixed originating tool identifier",
  "scope": { "date_from": "YYYY-MM-DD", "date_to": "YYYY-MM-DD", "metric": "positiveLogRecords" },
  "revision": "a hash of the source projection",
  "completeness": "complete",
  "refreshToken": "opaque authenticated query token",
  "nextCursor": null,
  "totalCount": null,
  "recovery": null
}
```

Scope contains resolved absolute dates and the original target and filters. Calendar
scope includes the original UTC window and calendar selection. Its date inputs
resolve midnight in the account's timezone; omitted dates capture the existing
forward feed window once. State reads such as account settings continue querying
the same state fields. Lifetime fields remain lifetime reads alongside a fixed
period, rather than becoming historical snapshots of account settings.

Revisions hash source membership and facts, including counts and identifiers where
the source exposes them. Deleting a source can change the revision even if the
remaining maximum update timestamp is unchanged. Answer time is never a source
revision. Reference-list chat reads bypass reference caches. Cached retrospective
narratives are marked `stale`; exact refresh calculates facts without generating
another narrative. Calendar disconnection is `partial` with `recovery: "reconnect"`.
A paged result is `partial` with an authenticated `nextCursor`. Partial refers to
the original query's result, rather than an invented full-data claim.

`POST /api/chat/reads/refresh` accepts:

```json
{
  "token": "read.refreshToken or read.nextCursor",
  "page": false,
  "clientContext": { "supportsParts": true, "partKinds": ["periodTotal"] }
}
```

Success returns `{ kind, block, read }`. For existing kinds, send their existing
card capability boolean as well. Habit lists containing skipped rows also require
`supportsSkippedHabitRows: true`. Dated habit lists additionally require
`supportsDatedHabitListCard: true`; habit history also requires
`supportsHabitHistoryCard: true`. Every refresh rechecks these capabilities, including
when a formerly current-day query becomes historical after local midnight. Set `page: true` only with `nextCursor`. Refresh
starts the original query at its first page. Paging preserves the query identity
and rejects a changed source revision with HTTP 409 (`READ_QUERY_STALE`); refresh
the original token before paging again. Refresh retains the query identity and can
return a changed revision. It cannot change the metric, windows, filters or target.
Each request authenticates the caller and repeats the original operation policy,
ownership, plan, scope and feature checks. A token grants no extra authority.

Tokens are bound to the user, authenticated with a domain-separated key derived
from `Jwt:SecretKey`, and expire after 30 days. All API instances use the existing
shared JWT configuration. Key rotation invalidates outstanding read tokens.
Invalid, expired or cross-user tokens return HTTP 410 (`READ_QUERY_EXPIRED`);
repeat the original request. Permission changes return HTTP 403. A removed target returns HTTP 404, and key
reads preserve their HTTP 428 step-up requirement. A source or refresh
failure returns HTTP 503 (`READ_QUERY_REFRESH_FAILED`); retain the visible prior
answer and retry the same token. Never substitute today's query after a failure.

`get_habit_history` uses the existing `recordList` kind with `kind: "habitHistory"`.
It additionally requires `supportsHabitHistoryCard: true`; the existing
`supportsRecordListCard` and `partKinds: ["recordList"]` alone are insufficient.
Without subtype support, both legacy and parts replies retain readable log dates
and record markers in text.
Each row has the log id, `logDate` and its actual `value`; these are record markers,
not measured amounts. Results sort by date descending, then id, and use the exact
refresh route for paging. Goal rows preserve their requested status/search filters.
Calendar blocks preserve connection/sync state and append the actual `calendars`
and `suggestions` arrays. Suggestion cutoffs and calendar selection remain bound
to the original query. Profile rows include the queried preference values, and
plan allowance rows use the subscription result. Key rows retain safe prefixes
and state only, with the existing step-up checks. Chat reads do not provision
referral codes, reconcile achievements, persist calendar connection state, create
recaps or shares, or write habit logs. Missing referral codes and links stay empty.

The paired renderer work belongs to thomasluizon/orbit-tickets#1619. Deploy these
append-only producer contracts before that consumer advertises the new kinds.
#1617 owns composition policy and #26 owns bilingual pipeline composition checks.
