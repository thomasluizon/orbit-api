# Habit month answers

Tracks thomasluizon/orbit-tickets#1747. Deploy this API before the paired UI
advertises `habitMonth` in `clientContext.partKinds` with `supportsParts: true`.
Existing clients receive a readable localized schedule and logs. The parts and
status-block kill flags retain their existing meaning. No minimum client version
change, migration, feature flag activation or credential is required.

`get_habit_month` reads Orbit habits. `get_calendar_overview` reads Google Calendar
events, meetings, connection and sync. The prompt distinguishes both intents in
English and Brazilian Portuguese without a keyword router or tool removal.

The optional `date_from` and `date_to` arguments are inclusive local ISO dates.
Without dates, the period is the user's complete current month, including future
days. An end date alone selects its month's first day; a start date alone selects
its month's last day. Explicit windows must be ordered and cover at most 62 days.
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

The model continuation receives only the inclusive window, user-local today and
one summary per habit, including nested children: identity, title, scheduled,
completed, skipped and missed day counts. Counts use the calendar's dates and
logs, with positive logs taking precedence over skips on the same day. Future
and current unlogged dates are not missed. Positive logs for habits being reduced
are reported as slip days, never completions or missed days. Flexible habits are
identified; their day counts describe calendar cells, not frequency targets.
The complete `CalendarMonthResponse` stays in the block, refresh and legacy text
fallback. The shared model payload cap shortens oversized results with retained
facts and omission metadata.

## Refresh contract for the paired UI

Tracks thomasluizon/orbit-tickets#1748. Refresh with
`POST /api/Chat/habit-month/refresh` in both HTTP API versions. Send the original
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

Replay negotiates the new kind again and falls back to localized text when the
client does not advertise it. A successful single month answer has one short
localized introduction with no figures, then its block. It also survives a failed
model continuation. Mixed reads retain their produced order and existing outcomes.

Google event rows omit `calendarName` when it contains an email address, preserving
friendly names such as Work. Event identifiers, titles and time fields retain their
contract. Gated optional sync reads still allow events and omit the gated sync row.
The API prompt forbids a vague availability warning: identify the actual unavailable
action and the returned reason, or omit it. The sentence reported by the owner is
not an API literal; its consumer rendering belongs to the paired UI change.

## Cases for the existing Astra eval

The live corpus is owned by #26 in `orbit-ui-mobile/evals/astra`. This API-only
change supplies the cases for the coordinator and paired UI ticket to add there;
unit tests do not claim to prove a real model's tool choice.

| Locale | Prompt | Expected tool and block |
| --- | --- | --- |
| en | Show my habit calendar for this month | `get_habit_month`, `habitMonth` |
| en | Show my month | `get_habit_month`, `habitMonth` |
| en | How did my month go day by day? | `get_habit_month`, `habitMonth` |
| pt-BR | Mostre meu calendário de hábitos deste mês | `get_habit_month`, `habitMonth` |
| pt-BR | Mostre meu mês | `get_habit_month`, `habitMonth` |
| pt-BR | Como foi meu mês dia a dia? | `get_habit_month`, `habitMonth` |
| en | Show my calendar events this month | `get_calendar_overview`, `calendar` |
| en | What meetings do I have this month? | `get_calendar_overview`, `calendar` |
| pt-BR | Mostre meus eventos do calendário deste mês | `get_calendar_overview`, `calendar` |
| pt-BR | Quais reuniões tenho neste mês? | `get_calendar_overview`, `calendar` |

On staging, the coordinator checks all cases against the real model, parity with
Calendário on the same account, and current-message language on a pt-BR profile
receiving an English request. The paired UI ticket renders `habitMonth` as that
grid, removes the vague actions sentence, and suppresses account emails in any
consumer fallback. No browser or server is started by this worker.

## Copy for review

The coordinator's `/second-opinion` approval covers these new user-facing strings:

| Use | en | pt-BR |
| --- | --- | --- |
| Introduction | Here is your habit calendar. | Aqui está seu calendário de hábitos. |
| Legacy heading | Habit calendar | Calendário de hábitos |
| Legacy log legend | Logs: positive = completion, zero = skipped; for bad habits, positive = slip. | Registros: positivo = conclusão, zero = pulado; em hábitos ruins, positivo = deslize. |
| Invalid period (`HABIT_CALENDAR_RANGE_INVALID`) | Choose a valid habit calendar period of up to 62 days. | Escolha um período válido de até 62 dias para o calendário de hábitos. |
