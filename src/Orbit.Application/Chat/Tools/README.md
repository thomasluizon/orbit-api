# Chat AI Tools

Each chat tool is a small adapter. It exposes one capability to the AI through a JSON schema and calls the application logic, usually a MediatR command or query.

## Contents

- [IAiTool contract](#the-iaitool-contract)
- [Ordering](#tool-ordering-order)
- [Catalog mapping](#catalog-mapping-is-mandatory)
- [MCP bridge](#mcp-routes-through-the-same-tools)
- [Habit query results](#typed-habit-query-results)
- [Period metrics](#period-metric-definitions)
- [Total and comparison](#total-and-comparison-blocks)
- [Read metadata](#exact-read-metadata-and-recovery)

## The `IAiTool` contract

A tool implements `IAiTool` (`IAiTool.cs`):

- `string Name`: the stable tool name that the model calls. It is also the agent operation id, so it must be unique across all tools.
- `string Description`: what the tool does and when to use it.
- `bool IsReadOnly` (default `false`): a read-only tool never changes state. The catalog uses it for `IsAgentExecutable` and audit.
- `int Order` (default `int.MaxValue`): the execution priority in one tool-call iteration.
- `object GetParameterSchema()`: the JSON schema of the arguments. Build it from the `JsonSchemaTypes` constants.
- `Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)`: validate the arguments at the boundary, do the work and return a `ToolResult` (`Success`, `EntityId`, `EntityName`, `Error`, `Payload`).


## Tool ordering (`Order`)

When the model requests several tool calls in one iteration, `ProcessUserChatCommand.ProcessToolCallsAsync` runs them in ascending `Order`.

| Order | Tool               | Why                                              |
| ----- | ------------------ | ------------------------------------------------ |
| 0     | `create_habit`     | Parent habits must exist before sub-habits and tags. |
| 1     | `create_sub_habit` | A sub-habit depends on its parent.               |
| 2     | `assign_tags`      | Tags attach to a habit that must already exist.  |

Every other tool keeps `int.MaxValue` and runs last. `OrderBy` is stable, so tools with the same `Order` keep their call order.

## Catalog mapping is mandatory

Each registered `IAiTool` must have a matching `chatTools` entry on an agent capability in `AgentCatalogService`. At startup, `BuildOperations` throws `InvalidOperationException` for a tool name that no capability maps. Add a new tool together with its catalog `chatTools` entry and its DI registration in `ServiceCollectionExtensions`.

## MCP routes through the same tools

The mutating MCP tools (`Orbit.Api/Mcp/Tools/*`) do not call MediatR. They call `McpExecutorBridge`, then `IAgentOperationExecutor`. The executor finds the `IAiTool` by name and the matching catalog operation. It applies the same policy: read-only credential denial, ownership pre-check and confirmation gating. It writes an `AgentAuditLogs` row. Chat and MCP share one implementation and one policy for each capability. The read-only MCP tools stay on MediatR.

## Typed habit query results

`query_habits` keeps its formatted `EntityName`. It also returns a `HabitListCard` payload from the same filtered schedule result. Rows keep the order, the hierarchy and the completion status of the query. The optional `date` is the resolved user-local day.

The scope is `date` for a past or future schedule, `today` for the current day and `all` for an undated query. An undated query keeps its filters. `all` does not mean that the payload holds every habit.

Habit rows append the optional nullable boolean `isSkippedOnDate`:

- A dated query sets it to true for a skipped occurrence and to false for the others. An undated query omits it.
- The enclosing `date` stays authoritative after refresh and replay. A skipped row keeps the `status` `none`.
- A question about the completion or skip state of a day uses `query_habits` with `date` and a null `is_completed`.
- A habit list with a skipped row also requires `clientContext.supportsSkippedHabitRows: true`. Without it, the response keeps the localized typed skip text, also on stored replay.

The normal page size is 50, also for an all-habits request. A result of 51 rows with limit 50 is partial and has a signed next cursor. A complete empty read keeps its `search` in the query metadata and adds one short localized scope sentence after the empty block.

Each read tool call gets its own query identity. The `readDirective` of a continuation result, for example `[[orbit:read:call_1]]`, refers only to that call. An unqualified legacy directive cannot choose between results with different scopes. An ambiguous directive suppresses only its own binding.

A `habitList` with `scope: "date"` also requires `supportsDatedHabitListCard: true`. Without it, the response holds dated text from the real rows and no live habit controls.

## Period metric definitions

`get_period_analytics` requires `metric`. It also requires `relative_period` or both `date_from` and `date_to`. The dates are inclusive user-local days that the server resolves before the query. The arguments `habit_id`, `search`, `tag` and `is_bad_habit` limit the source.

- Record, date and schedule aggregates include root habits. A child that the user selects is measured alone.
- `completedHabits` counts an independent child from its own logs only.
- The retrospective enums keep their rolling definition. `month` means the last 30 days.

`RelativeReadPeriod` lists the relative selectors. A counted rolling window uses `last_n_days` or `last_n_weeks` with `period_count`. A current week or month ends at the local today of the account. A previous week or month is complete. Week boundaries follow the week-start preference. A comparison uses `baseline_relative_period` and the optional `baseline_period_count`. A relative selector overrides the absolute dates of the model. The server removes it before the query, so read metadata, paging and refresh keep the exact absolute scope.

`query_goals`, `review_goals` and `get_gamification_overview` return current snapshots, not period totals. Their `as_of` anchor uses the local today of the account. A question about a period habit streak uses `get_period_analytics` or `get_habit_metrics`. `query_habits` accepts only the single-day selectors `today` and `yesterday`.

| Metric | Unit | Definition |
| --- | --- | --- |
| `completedHabits` | `habits` | Distinct nondeleted positive recurring or flexible habits with at least one positive, nondeleted, nonslip log in the window. Includes ended and completed habits. Excludes tasks, General items and bad habits. |
| `positiveLogRecords` | `records` | Number of nondeleted positive log records in the window, including unscheduled records. |
| `distinctLoggedDates` | `days` | Number of distinct local dates with positive records across the selected habits. |
| `metScheduledDates` | `days` | Number of scheduled habit and date pairs with a positive record on that date. |
| `scheduledDates` | `days` | Scheduled habit and date pairs, limited by the creation, recurrence and end dates in the account timezone. General and bad habits have no positive-adherence denominator. |

`PeriodHabitMetrics` holds `window: { from, to }`, the four counts, the optional nullable `completedHabits`, `completionRate` (0 to 100, null without scheduled dates), `currentStreak`, `bestStreak` and `streakScope: "period"`. These streaks count consecutive dates with positive activity inside the window. The current streak ends at the last date of the window. They are not the lifetime habit streak or the account streak. The series uses daily buckets up to 31 days, then weekly buckets that follow the week-start preference. The first and last buckets are clipped. The summed series counts equal the scheduled and met counts.

`get_habit_metrics` keeps every lifetime field and appends `period`, `periodSeries` and `sourceRevision`. The optional `date_from` and `date_to` select the period. When both are absent, the period is the 30 local days that end today. The query captures `as_of` once for the rolling weekly and monthly rates and the current streak, and refresh keeps it. The revision includes the timezone and the week-start setting of the account.

A period answer holds at most one short sentence without figures, then the block. Figures belong only in the block. MCP exposes the same query through a typed `query` object with camelCase fields and the same dates and metric names.

## Total and comparison blocks

Both kinds require `supportsParts: true` and their exact kind in `partKinds`. For `metric` `completedHabits` with `unit` `habits`, both kinds also require `clientContext.supportsCompletedHabitsMetric: true`. When it is absent, null or false, the response holds a readable server text answer instead of the block, also on replay. The other period metrics keep their negotiation.

`AiPeriodBlocksDisabled` suppresses both kinds. They need no legacy directive. When the AI continuation is unavailable, a successful focused analytics query still returns its blocks or deterministic text with the exact windows, counts and units. A failed source query stays a failure.

`periodTotal` holds `metric`, `unit`, `value`, `metrics`, `series`, the nullable `habitId` and `read`. `metrics.window` names the resolved dates. The metrics include the met numerator and the scheduled denominator.

`periodComparison` needs both `baseline_from` and `baseline_to`. It holds `metric`, `unit`, `current`, `baseline`, `currentValue`, `baselineValue`, `absoluteDelta`, the nullable `relativeDelta`, the nullable `habitId` and `read`. `current` and `baseline` are complete `PeriodHabitMetrics` values with their own windows and denominators. The optional `completedHabits` count is on `periodTotal.metrics`, `periodComparison.current` and `periodComparison.baseline`. A missing or null count from an older producer means unknown, not zero.

The absolute delta is current minus baseline. The relative delta is the absolute delta divided by the baseline, as a ratio and not as a percentage. A zero baseline gives a null relative delta.

## Exact read metadata and recovery

`AgentOperationResult` appends the optional `access` field with `"read"` or `"write"` for each known tool or capability, also for a failed or policy-denied operation. Chat responses and final stream responses use the same field. A missing field means unknown. A client uses it to tell a read failure from a write failure.

Each affected card type appends the optional `read` metadata. `AgentOperationResult` optionally holds `readBlock: { kind, block, read }`. The metadata is:

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

The scope holds the resolved absolute dates, the original target and the filters. The calendar scope also holds the original UTC window and the calendar selection. The server captures an omitted calendar date once from the forward feed window. A lifetime field stays a lifetime read next to a fixed period.

A revision hashes the membership and the facts of the source. The deletion of a source can change the revision. The answer time is never a revision. A cached retrospective narrative is `stale`, and an exact refresh calculates the facts without a new narrative. A disconnected calendar is `partial` with `recovery: "reconnect"`. A paged result is `partial` with an authenticated `nextCursor`. `partial` describes the original query result only.

`POST /api/chat/reads/refresh` accepts:

```json
{
  "token": "read.refreshToken or read.nextCursor",
  "page": false,
  "clientContext": { "supportsParts": true, "partKinds": ["periodTotal"] }
}
```

A success returns `{ kind, block, read }`. For an existing kind, send its card capability boolean. A habit list with skipped rows also needs `supportsSkippedHabitRows: true`. A dated habit list also needs `supportsDatedHabitListCard: true`. Habit history also needs `supportsHabitHistoryCard: true`. Each refresh checks these capabilities again, also when a current-day query becomes historical after local midnight.

- Set `page: true` only with `nextCursor`.
- A refresh restarts the original query at its first page.
- Paging keeps the query identity. It rejects a changed source revision with HTTP 409 (`READ_QUERY_STALE`). Refresh the original token before the next page.
- A refresh keeps the query identity and can return a changed revision. It cannot change the metric, the windows, the filters or the target.
- Each request authenticates the caller and repeats the policy, ownership, plan, scope and feature checks of the original operation. A token grants no extra authority.

A token is bound to the user. A domain-separated key from `Jwt:SecretKey` authenticates it, and it expires after 30 days. All API instances use the same JWT configuration. A key rotation invalidates the outstanding tokens. The error codes are:

- HTTP 410 (`READ_QUERY_EXPIRED`): the token is invalid, expired or from another user. Repeat the original request.
- HTTP 403: a permission changed.
- HTTP 404: the target was removed.
- HTTP 428: a key read keeps its step-up requirement.
- HTTP 503 (`READ_QUERY_REFRESH_FAILED`): the source or the refresh failed. Keep the visible prior answer and retry the same token. Never substitute the query of today.

`get_habit_history` uses the `recordList` kind with `kind: "habitHistory"`. It also requires `supportsHabitHistoryCard: true`. Without it, legacy and parts replies keep the log dates and record markers as text. Each row holds the log id, `logDate` and its `value`, a record marker and not a measured amount. Results sort by date descending, then by id.

Other read blocks:

- Goal rows keep their status and search filters.
- Calendar blocks keep the connection and sync state and append the `calendars` and `suggestions` arrays, bound to the original query.
- Key rows hold only safe prefixes and the state, with the step-up checks.
- A chat read does not create referral codes, reconcile achievements, save the calendar connection state, create recaps or shares, or write habit logs. A missing referral code or link stays empty.
