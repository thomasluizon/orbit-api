# Habit ranking consumer contract

Tracks thomasluizon/orbit-tickets#1615. The consumer is owned by
thomasluizon/orbit-tickets#1618; composition is owned by
thomasluizon/orbit-tickets#1617. Deploy this API before enabling the consumer.
Select a full producer commit and a supported identity from
[artifacts.json](artifacts.json), following [README.md](README.md).

## Query and window

`get_habit_ranking` is a read-only chat tool. It does not call retrospective
narrative, streak recalculation, recap persistence, sharing or mutation commands.
The registered capability is `habits.ranking.read`, requiring `read_habits`.
The MCP tool has the same `get_habit_ranking` name and accepts a nested `query`
object with camelCase fields matching the HTTP/query contracts, including
`queryToken`, `cursor` and `refresh`. It dispatches the same read-only handler
and returns the response as JSON, preserving capability parity.

The tool accepts `date_from` and `date_to_exclusive`, or `window` and `months`.
There is no implicit time window: ask the person to resolve a missing window.
`window` is `rolling_months` or `complete_months`; `months` is 1 through 120.
Dates use the authenticated user's timezone and a start-inclusive, end-exclusive
interval `[dateFrom, dateToExclusive)`. An explicitly inclusive end advances by
one local calendar day. The range must contain 1 through 3660 days.

For “últimos 6 meses” or “last six months”, resolve `rolling_months`, `months: 6`
using `[userToday.AddMonths(-6), userToday.AddDays(1))`. Calendar subtraction
clamps the day to the destination month's last day. For example, August 31 maps
to February 29 in a leap year and February 28 otherwise. This includes today.
An explicitly requested six complete calendar months uses the first of the
current local month as the exclusive end and subtracts six months from that
first day. Never replace either operation with 180 days or reinterpret the
legacy retrospective `semester` period.

The sole supported metric is `completion_log_records`: count distinct positive
completion log records by log ID, excluding undone, soft-deleted and slip logs.
Multiple independent records on one local date each count; delivery of the same
record counts once. This is neither `distinct_logged_days` nor
`met_scheduled_dates`; requests for those metrics are rejected, not silently
reinterpreted. Current goal targets and schedules do not change this count.

The fixed scope is positive recurring habits, including independently logged
children. Parent rows count only their own records. Tasks, bad habits, slips,
soft-deleted habits and their logs are excluded. Orbit has no archive state.
Ended and completed recurring habits retain their eligible records and remain
ranked. Rows with zero eligible completions are omitted, so no completions yields
an ordinary successful empty ranking.

Optional `habit_ids` filters to up to 100 distinct owned, eligible targets; an
unowned, missing or ineligible target on an initial query returns 404. Omission
or an empty array means all eligible habits. A target subsequently removed
remains in the bound filter but cannot reappear in refreshed results.
`page_size` defaults to 20 and must be 1 through 100. The full eligible set is
sorted before paging by count descending, then canonical UUID text ascending.
Ranks are absolute, start at one and remain distinct across ties.

## HTTP reads

All routes require authentication. API keys additionally need both `chat_interact`
and `read_habits`. JWT identity supplies the user; requests cannot provide a user ID.
Both HTTP API versions expose these additive routes:

| Request | Input | Output |
| --- | --- | --- |
| `POST /api/chat/habit-ranking/query` | JSON `dateFrom` plus `dateToExclusive`, or `window` plus `months`; optional `metric`, `habitIds`, `pageSize` | `HabitRankingResponse` |
| `GET /api/chat/habit-ranking?queryToken=...&cursor=...` | Unchanged response `queryToken` and `nextCursor`, URL encoded | Next `HabitRankingResponse` |
| `GET /api/chat/habit-ranking?queryToken=...&refresh=true` | Unchanged response `queryToken`; omit cursor | New first page for the same absolute query |

Chat paging uses `get_habit_ranking` with `query_token` plus `cursor`, or
`query_token` plus `refresh: true`. Do not repeat or override dates, month windows,
metrics, filters or page sizes with a bound query. The validator rejects overrides.

A query token authenticates the owner, resolved dates, timezone, metric, target
IDs and page size with a dedicated purpose and the existing `Jwt:SecretKey`.
A cursor authenticates the query identity, revision and next offset. Tokens are
opaque client values and remain usable until the signing key changes. They are
never write approval or authentication credentials; every request still requires
current authentication and scopes. No query or result is stored in the database.

Each page re-reads current owned data and verifies the complete ranking revision.
If eligible logs or displayed row data changed, paging returns HTTP 409 with
`errorCode: HABIT_RANKING_STALE`. Refresh with the original query token, replace
all accumulated pages, and then use only the refreshed cursors. Refresh never
resolves a relative window again, including after midnight or a timezone change.
Invalid/tampered/cross-user/cross-query tokens, invalid bounds and unsupported
metrics return 400 with `HABIT_RANKING_INVALID` (validation pipeline failures may
instead carry field errors). No eligible records returns HTTP 200 and empty rows.

## Response and block

`HabitRankingResponse` has this shape. Date strings, tokens and hashes below are placeholders;
clients must use the exact returned strings.

```json
{
  "queryId": "query-hash",
  "queryToken": "signed-query-token",
  "revision": "ranking-revision-hash",
  "query": {
    "dateFrom": "YYYY-MM-DD",
    "dateToExclusive": "YYYY-MM-DD",
    "timeZone": "America/Sao_Paulo",
    "metric": "completion_log_records",
    "countDefinition": "Count of distinct positive completion log records; undone, deleted and slip records excluded.",
    "scope": {
      "entityType": "positive_recurring_habits",
      "includeChildren": true,
      "parentCounts": "own_logs_only",
      "inactiveHabits": "include_ended_and_completed",
      "softDeletedHabits": "exclude_with_logs",
      "tasksAndSlips": "exclude",
      "zeroCompletions": "exclude"
    },
    "habitIds": [],
    "pageSize": 20,
    "boundaryConvention": "start_inclusive_end_exclusive"
  },
  "rows": [
    {
      "rank": 1,
      "habitId": "00000000-0000-0000-0000-000000000001",
      "name": "Read",
      "emoji": null,
      "parentHabitId": null,
      "completedCount": 42
    }
  ],
  "totalCount": 1,
  "offset": 0,
  "isComplete": true,
  "nextCursor": null
}
```

`queryId` is a deterministic hash of the normalized owned query. `revision`
identifies the complete ordered result, including its eligible log identities
and row labels. Each page repeats the complete resolved query and total.
`offset` is the zero-based number of preceding rows. `isComplete` means this
page reaches the end; `nextCursor` is null exactly when complete. A consumer
has the whole ranking after collecting pages from offset zero through that end.
Empty results have total zero, offset zero, complete true and null cursor.

For a client advertising `supportsParts: true` and including the exact
`habitRanking` string in `partKinds`, the server emits:

```json
{
  "type": "block",
  "kind": "habitRanking",
  "block": {
    "ranking": { "...": "the complete HabitRankingResponse above" },
    "surfaceId": "progress"
  }
}
```

The model directive is `[[orbit:ranking]]`. The server builds its payload from
the successful query result. If either capability is missing, it supplies readable
text containing the resolved window, record-count definition, rows and page
position. It never relies on an older Android client skipping an unknown kind.
A query-only ranking turn can return the same block or text if the model's
follow-up narrative call fails. A failed block build returns readable retry text.
An initial model outage cannot resolve a natural-language request; the HTTP reads
remain deterministic and available independently.

## Activation, rollback and verification

Ship the API and its generated contracts first, then let #1618 advertise both
capabilities. Removing `habitRanking` from `partKinds` disables the structured
path. Existing `astra_period_blocks_disabled` and `astra_parts_disabled` switches
also retain the readable fallback. Revert the producer if count correctness fails.
There is no minimum-version raise, schema migration, new secret or backfill.
Existing retrospective fields, tools, enums and period blocks retain their behavior.

Events `habit_ranking_query` and `habit_ranking_block` use existing telemetry:
fixed kind and metric IDs, result count, completeness, query/page/refresh outcome
and build failure. They contain no titles, prompts, filters or cursor values.
No per-call cost instrumentation is added.

The repository contract-aligner instructions were reviewed for endpoint and
nullable-field parity. Consumer edits and consumer contract tests belong to
#1618; this API work does not change a second repository or reuse historical
branch instructions. Unit tests cover query, domain guards, token verification,
repository translation/decryption, chat capabilities, narrative outage and
controller payloads. #26 owns real-pipeline bilingual and composition acceptance.
