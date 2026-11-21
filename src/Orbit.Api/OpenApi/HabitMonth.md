# Habit month answers

A client receives the part only when `clientContext.supportsParts` is true and
`partKinds` includes `habitMonth`. Other clients receive a readable localized schedule.

The tool requires `window`: `month` or `date_range`. A `month` window is the complete
local month, including future days, and `relative_period` (`this_month` or
`last_month`) selects it; `date_from` and `date_to` are optional inclusive local ISO
dates, and one date alone selects the month that holds it. A `date_range` window requires
both dates, with `date_to` after `date_from`. A window covers at most 62 days.
`language` is `en`, `pt` or `pt-BR`, taken from the person's current message.
The account date and week start come from `IUserDateService`.

The returned part has `kind: "habitMonth"` and a `HabitMonthCard` block:

| Field | Meaning |
| --- | --- |
| `dateFrom`, `dateTo` | Inclusive local period |
| `today` | User-local today for missed versus future states |
| `weekStartDay` | Existing Sunday or Monday preference |
| `calendar` | The exact `CalendarMonthResponse` used by Calendário |
| `calendar.habits` | Ordered schedules, scheduled dates, instances and nested children |
| `calendar.logs` | Logs keyed by habit UUID, including zero-value skips |
| `language` | Current message locale |
| `surfaceId` | Existing `calendar` destination |
| `read` | Exact query identity, scope, revision and refresh token |

Reuse the Calendário grid with `calendar.habits` and `calendar.logs`. Preserve
schedule-specific semantics for flexible habits and habits being reduced.
An absent log on a past scheduled day remains available for missed-day rendering;
zero remains a skip, and future dates remain future. Do not reinterpret every
instance marked Completed as a positive log: the existing instance projection also
uses that state for skips. No grid aggregation or replacement schedule engine is
introduced in chat. The complete month is returned without sampling or paging.

The model continuation receives only the window, user-local today and one summary
per habit, nested children included: scheduled, completed, skipped and missed day
counts. A positive log wins over a skip on the same day. Future and current unlogged
dates are not missed. Positive logs of habits being reduced count as slip days, never
as completions or missed days. Flexible habits are identified; their counts describe
calendar cells, not frequency targets.
The complete `CalendarMonthResponse` stays in the block, refresh and text fallback.

## Refresh

Refresh with `POST /api/Chat/habit-month/refresh` in both HTTP API versions. Send the original
`read.refreshToken` and the negotiated client context:

```json
{
  "token": "<read.refreshToken>",
  "clientContext": {
    "supportsParts": true,
    "partKinds": ["habitMonth"]
  }
}
```

The response is `HabitMonthReadBlockRefreshResponse`: `kind: "habitMonth"`,
`block: HabitMonthCard` and `read: ReadBlockMetadata`. Replace the card and its
read metadata together. Refresh retains the original absolute dates across month
and timezone boundaries. The complete period is returned without a page parameter.
The route requires the authenticated owner; API key and OAuth credentials need
both `chat_interact` and `read_habits`. Existing token expiry, scope, policy and
parts/status-block kill flags apply to every refresh.

`POST /api/Chat/reads/refresh` retains its published response union and rejects
habit-month tokens before executing the read. Do not use that route for this kind.

Replay negotiates the kind again and falls back to localized text when the client
does not advertise it. A successful single month answer has one short introduction
with no figures, then its block, and survives a failed model continuation.
