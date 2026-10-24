# Proactive answer parts

`GET /api/ai/proactive/current` accepts optional `clientContext` as a URL-encoded
JSON query parameter. Its object is the same `AgentClientContext` used by chat.
For example, before URL encoding:

```json
{"supportsParts":true,"partKinds":["habitList","goalList","daySummary","calendar","periodInsight"],"supportsHabitListCard":true,"supportsGoalListCard":true,"supportsDaySummaryCard":true,"supportsCalendarCard":true,"supportsPeriodInsightCard":true,"supportsHabitListDoneStatus":true,"supportsDatedHabitListCard":true}
```

Both HTTP API versions append optional `parts` to `ProactiveItemResponse` using
the existing `ChatResponsePart` union. A supported response carries exactly one
`type: "block"` part, without additional narrative. All existing response fields
keep their original meaning and values. Omitted or false `supportsParts` omits
`parts` entirely and performs no additional block reads. An empty current item
still returns HTTP 204. Malformed or JSON-null client context returns HTTP 400.

| Trigger | Kind | Authoritative read and scope |
| --- | --- | --- |
| RestartAfterMiss | daySummary | Previous local day's summary, including habit rows |
| EveningCheckin | habitList | Remaining good habits on the item's local day, including overdue habits |
| MorningPlan | habitList | Remaining good habits scheduled on the item's local day |
| BusyCalendarDay | calendar | Events in the item's local day from the user's selected calendars |
| GoalBehind | goalList | Active goal review with current progress and projections |
| WeeklyReview | periodInsight | The saved review interval, with the previous seven local days as the legacy fallback |

Every emitted trigger has a matching existing block family. Habit and goal rows,
calendar events and metrics come from the same read operations as chat. Sparse
saved facts select the review interval; they never stand in for a complete card.
Summary and retrospective reads disable narrative generation. A scheduled week
with zero logs remains a valid data-only retrospective.

The client must advertise the exact kind in `partKinds` and its existing card
capability. Chat's `astra_parts_disabled` and per-family flags apply, as do its
done-status and goal-projection adaptations. Unsupported kinds, disabled parts,
failed reads and policy denials retain the legacy response without a block.
Credential method, granted scopes and read-only restrictions reach the existing
agent operation policy checks.

Cards carry the existing `read` metadata with query ID, absolute scope, source
revision, refresh token, completeness and pagination where available. Use
`POST /api/chat/reads/refresh` with the original token and client context, and
`page: true` with `read.nextCursor` for the original query's next page. Refresh
retains the original dates when the user's local day advances. A habit list that
becomes historical needs `supportsDatedHabitListCard`. Existing refresh-route
authentication, scope and feature checks continue to apply.

All four supported OpenAPI artifacts publish the append-only field and query
parameter. The paired consumer must pin the deployed producer commit and catalog
identity, update its shared snapshot and `ProactiveItemResponse` schema, send the
negotiation context on web and Android, and render `parts` when present. Deploy
the API to staging and production before enabling the carrying clients.

Tracks thomasluizon/orbit-tickets#1662. The paired consumer rendering belongs to
thomasluizon/orbit-tickets#1620.
