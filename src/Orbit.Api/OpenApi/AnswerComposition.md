# Astra answer composition

Tracks thomasluizon/orbit-tickets#1617. The API owns composition; #1620 owns the consumer
and #26 owns real-call bilingual assertions in `evals/astra/`. No second eval harness applies.

The database flag `astra_block_first` is seeded off. Composition requires `supportsParts`
after kill flags, advertised `partKinds`, and existing card/subtype capabilities. Disabling
the flag restores negotiated directive assembly. Legacy clients keep FORMAT and all fields.

Successful typed results supply blocks and query identities. Directives cannot establish
success or determine the leading position. Tool-call structure selects answers without request
keywords. A lookup is a prerequisite when all its typed entity IDs first appear in that result
and supply arguments to later tool rounds. Calls in the same round cannot consume each other's
results. The model selects requested subjects through its tool calls and arguments;
the server never filters reads by words in the person's message. Successful reads render in recorded operation order, including independent reads
alongside a pending preview or write outcome. Rankings use the complete count result and retain
their produced position. Only equivalent query families, kinds and canonical scopes deduplicate;
different entity IDs, date windows and metrics remain distinct, including history scopes.
The server neither queries an implicit dashboard nor derives numbers from prose.

A validated question form containing one single-choice field becomes a `choice`
when its options fit the existing choice contract and the client supports it.
It asks the top-level question, then the field's prompt, and uses a heading only when
neither exists; option ids, labels and free-text permission are preserved. A heading,
introduction or progress display does not require a questionnaire. Several fields
retain the question form. See [AskValidation.md](AskValidation.md).

An ask cannot end a round with unfinished account reads. Those reads run, and
the model receives a typed failure for the premature question so it can finish
resolved actions through normal continuation. A genuine question can accompany
an independent write preview. Candidate ids and exact account titles protect
the unresolved targets; the server does not derive intent from message patterns.

| Family | Leading surface | Named consumer |
| --- | --- | --- |
| Habits and schedules | Query-bound `habitList` or `daySummary` | #1619 |
| Logs and history | `recordList` with negotiated `habitHistory` subtype | #1619 |
| Metrics, streaks, level and achievements | Focused `metrics` or `streak` | #1619 |
| Rankings | #1615 `habitRanking` | #1618 |
| Totals and comparisons | #1616 `periodTotal` or `periodComparison` | #1619 |
| Goals | Query-bound `goalList` | #1619 |
| Calendar and sync | `calendar`, including actual recovery state | #1619 |
| Notifications, tags, templates and keys | Relevant `recordList` subtype | #1619 |
| Profile, settings, plan and referrals | Relevant `accountRows` subtype | #1619 |
| Retrospectives | Focused `periodInsight`; narrative stays in its block | #1619 |
| Editable habit suggestions | `habitSuggestions`, without surrounding prose | #1751, #1620 |
| Writes and outcomes | Existing pending preview or action outcome, without repeated prose | #24, #1620 |
| Clarification and choices | `choice` or `pickList`; question lives in the block | #1620 |
| Proactive chat turns | Corresponding read block or short no-data nudge | #1620 |
| Empty, partial and stale | Relevant block retains completeness and supported recovery | #1619, #1620 |
| Failure, quota, plan, offline and refusal | Existing typed terminal outcome or concise status | #1620 |
| Help, coaching and small talk | One to three lines, at most 180 characters each, no headings or unsolicited bullets | #1620 |

Moderation replies and localized chat errors use an explicit `clientContext.locale`,
otherwise the account's profile language. `Accept-Language` does not become a client locale
or override a stored profile language. This precedence applies to legacy and parts clients
over both chat endpoints. Legacy moderation keeps HTTP 400 with `CONTENT_MODERATION_REFUSED`;
clients advertising `supportsMessageAllowance` keep the successful chat response with
`messageAllowanceConsumed: false`. Both paths use the existing English or Brazilian Portuguese
decline without a model call, tool execution, conversation recording or allowance consumption.

A single record selector uses `choice`, including when the client also supports
`question`. Question forms collect multiple fields. The server validates the complete
typed definition before negotiating a renderable single selector to `choice`.
Per-question free-text settings own question forms; the top-level flag applies to
choice only. Invalid ask calls return the precise failed constraint to the model
and continue through the normal tool loop within its round and token limits.
The continuation can read, prepare a write preview, retry a necessary question once,
or answer in prose. Only a validated, accepted ask ends the reply with a question
block; an invalid ask never discards recovery tool calls. The ask retry limit applies
to question attempts, not other tools. No question prepares a write. See
[ask validation and bilingual copy](AskValidation.md).

The failed-ask recovery path adds the following model-facing diagnostics. These
are tool feedback, not fixed replies to the person. Existing question retry and
plain-text fallback instructions remain available.

| en | pt-BR |
| --- | --- |
| The ask retry limit has been reached. | O limite de tentativas de ask foi atingido. |
| A failed ask did not issue a question. Continue the original request with the appropriate tools when its inputs are clear; no ask is needed for an actionable request. | A chamada de ask que falhou não emitiu uma pergunta. Continue o pedido original com as ferramentas adequadas quando as informações estiverem claras; um pedido que já pode ser atendido não precisa de ask. |

Supported data answers start with the typed block and omit framing prose. Choice questions stay in their blocks; pending previews retain
their existing narrative suppression. One short status may explain
truncation or failure alongside successful data. Missing capabilities receive readable text
from the typed result. Builder failure never becomes an empty success. Successful typed reads
survive narrative outage. Complete empty habit, goal and record lists retain their query-bound
typed block with empty items, zero total and complete metadata. The consumer renders its compact
localized empty state without a heading. They carry an existing screen action in `relatedSurfaces`
when applicable. With composition disabled, successful typed reads also survive an empty
continuation or grounding removing the entire narrative. Supported clients receive the produced
block; other clients receive its localized readable text. Recovery resets streamed text, preserves
the read operation and consumes no additional model call or message allowance. Habit queries use
`today`, the existing habit screen; `habits` is a data domain rather than a screen ID.
Partial and recovery states keep their relevant typed blocks. Unknown directives are stripped. Model text and follow-ups retain grounding.
Deterministic fallback text is already grounded by its typed producer.
Readable capability fallbacks omit list scope and calendar title headings. Counts, item names,
statuses, goal progress, date windows and calendar log meaning remain grounded facts. This
formatter serves both current composition and stored blocks downgraded during replay.
Single-day periods show one localized date. When that date is the account's current day,
the label is `Today` or `Hoje`, using `IUserDateService.GetUserTodayAsync`. Multi-day periods
retain both dates. The shared formatter applies this to summaries, totals, comparisons,
metrics, recaps, habit calendars and history. A stored typed block downgraded on a later day
uses its absolute date; an already composed text answer retains its original wording.
Habit listings use `query_habits`, and compound habit-and-goal listings pair it with
`query_goals`. `get_daily_summary` supplies requested completion totals or a narrative recap;
it does not replace the habit query. Tool descriptions and read instructions establish this
distinction without routing messages by keywords or rewriting the model's tool calls.
Negotiation resolves an explicit client locale before the profile language. A legacy client
without a locale uses the profile language; no message pattern detects the language. Pending
preview, empty-answer recovery, tool failure, truncation and habit-selection fallback copy use
the existing `ErrorCopy` catalog in English and Brazilian Portuguese. Pending previews still
require approval and never claim that a write has executed.

`describe_feature` returns its existing markdown and metadata with an append-only model-facing
`answer_scope` instruction, also present in the tool description. Only documented mechanics,
screens, fields and steps support an explanation. `related_surfaces` identifies navigation
destinations and does not document editing instructions. A missing detail calls for a brief
acknowledgment in the person's language. This does not fabricate a UI route, execute another
tool, replace the model's answer or add a model round. Public chat DTOs and enums are unchanged.
The deterministic replay tests verify the tool result supplied to the narrative model and the
resulting composition. #26's live bilingual gate verifies whether the model follows that scope.
Habit metrics retain their period log counts and lifetime totals. The optional completion
series is emitted only when its scheduled-completion total represents the period's log count.
Backdated logs can predate known schedule history; those figures remain available without
an incompatible chart. The server never invents a schedule or changes a lifetime total to
make the chart agree.

For composition clients, each read tool requires the boolean `orbit_read_complete` argument.
It is true only when the current round fully answers the request and false for prerequisite
lookups or any remaining read or write. Independent reads belong in one round. This signal
travels in the structured tool call, so completion does not depend on assistant text accompanying
a tool call. The server removes the argument before execution and can finish declared-complete
read plans in later rounds as well. The server skips continuation only when every call declares
completion, every declared call ran, every tool is read-only, every result succeeded and has
a typed projection, and no clarification, write or pending operation remains. Invalid ranking
projections cannot complete the round. A prerequisite lookup or a request needing another read
or write declares false and keeps the tool loop. A failed terminal read or a missing projection
requests continuation. A successful `query_habits` read with a nonempty `search`,
`include_metrics: true`, complete metadata and zero total rows follows the same completion
rules. When declared complete, it yields the query-bound empty habit block without inventing
streak metrics. Missing-capability and legacy clients keep the localized missing-habit and
unavailable-streak text. Refresh and stored replay apply the same composition capability policy.
A prerequisite lookup keeps the tool loop even when empty,
so the model can request creation details or prepare the remaining change.
Habit lookups append the model argument `for_selection`. True declares a lookup for an
unanswered choice or pick list, including a single candidate. These typed results keep
the tool loop open for `ask` and block writes to those candidates until the person answers.
A searched lookup with multiple matches retains its candidate signal even when an ID
appears in a later write call. Ordinary list reads explicitly declare false and retain
their list projection. For clients advertising ask forms, a lookup without a declared
purpose requires continuation before it can be treated as the completed answer.
Write checks use resolved identifiers and the bulk operation's own filter selector.
Candidate writes are rejected before coalescing so unrelated writes still run. The final
effective calls are checked again before dispatch or preview creation.
No request-text pattern selects this behavior. See [HabitSelection.md](HabitSelection.md)
for the selection contract and the English and Brazilian Portuguese copy submitted for review.

An issued `ask` block ends the turn and suppresses read completion and readable read
fallbacks. Its question and options are the model's structured output, preserved without
message-based rewriting. A rejected ask can retry once with a supported form and valid
arguments. Only a lone model-emitted ask call can use that retry; it cannot dispatch a write
or restart the general tool loop. An unavailable ask surface falls back to text.
Ask instructions name the client's supported forms while the shared
tool schemas remain stable across clients. A model-emitted
single-choice question can use the existing choice surface when question blocks are unsupported;
its supplied question, option labels and free-text permission remain intact and pass the choice
validator and domain guard. No write is prepared before the answer. Habit clarification and streak selection live in the prompt and
tool contract.

The shared `ask` schema reserves `intro` for the question form. Choice and pick-list
projection omit that question-only introduction before validation, so an otherwise valid
selection still produces its typed block. Its title, items, details and action remain the
model's structured output. Item and combined bulk arguments pass the existing checks;
issuing the selection prepares no write. The pick-list response shape stays unchanged.

The full registered tool set remains available regardless of message wording or language.
Composition never renames calls, invokes an uncalled tool, or substitutes fixed advice.
Prompt instructions direct provider/connection/sync reads to `get_calendar_overview`, habit
month reads to `get_habit_month`, focused metrics to `get_habit_metrics`, profile/settings
to `get_profile`, and plan-feature help to `describe_feature`. Coaching uses the model's
short answer unless a record change is explicitly requested. These are model instructions,
not server routing rules. A mistaken tool selection remains visible as its actual typed
result or negotiated readable fallback; it cannot fabricate the expected family. A dependent
metrics lookup requires the model's subsequent metrics call. Names, permissions, dates and
calculations remain owned by the existing producers and every write retains approval.

Advice about starting an activity stays short text even when it mentions a date or possible
checklist. The model prepares writes only for requested record changes and reuses an existing
activity for an adjustment. Editable suggestions with a resolved `existingHabitId` use that
habit's stored title, including in their approval arguments; the model's proposed title cannot
rename it. A title match without an ID receives the same stored identity and title.

A successful typed `HabitSuggestionsCard` supplies the suggestions answer directly. When
`habitSuggestions` is negotiated, it is the only part and `aiMessage` is empty; the server
adds no introductory sentence. This applies with block-first composition enabled or disabled,
including declared-complete reads and reads that continue through a narrative round. Legacy
clients, missing-kind clients and the parts kill flag receive the existing readable draft
details. Negotiation happens before recording, and SSE final delivery and stored replay retain
the same composition. Suggestions remain drafts without pending writes or execution claims;
the existing preview routes still require approval. The captured tool-plan replays in
`G7SuggestionCompositionTests` cover both languages and capability fallbacks. #26 owns the
live bilingual gate rerun, including suggestion quality, and #1751/#1620 own rendering.

Ongoing avoid habits default to `frequency_unit: Day`, `frequency_quantity: 1` and
`is_general: false` unless the person requested another cadence. Creation schemas expose
that default for both root and nested items. The proposal validator rejects the incompatible
General and avoid combination before preparing a preview, matching the existing domain guard.
The typed failure asks the model to correct its arguments while retaining the rest of the request.

Temporary preparation commitments preserve the person's action wording and negation in their titles.
Medical instructions normalize only spelling and casing without expanding or substituting the verb.
An exact record title stays verbatim. For an activity described in prose, the model normalizes
spelling, casing and proper names while keeping its action and negation. Supplied event dates,
including past dates, anchor the preparation offsets and do not require another question.
Preparation continuing until an event uses daily recurrence with `due_date` at its start and
inclusive `end_date` at the event; `start_date` does not substitute for `due_date`. Each preparation
uses its own offset from its referenced event, independently of the other preparation items.
An activity ending at a collection uses its collection date and `due_end_time` for that deadline.
These are model instructions;
composition and execution preserve the structured arguments rather than rewriting message text.

Write completion covers every requested action, target and occurrence. A target lookup is a
prerequisite, not a mutation. A rename plus a skip uses both write tools; skipping several
habits uses `bulk_skip_habits`. A lookup showing an already skipped occurrence does not replace
the requested skip call: the matching write tool validates the current state. The completion
argument, query description and skip description carry this instruction to the model.
The server combines the model's held writes into one preview
and preserves their action labels. It never infers missing writes from the person's wording
or changes a log call into a skip call.

New suggestion drafts contain requested values and the fields required by the existing draft
contract. For new activities, unrequested weekdays and checklist items stay empty, and reminders stay null.
Adjustment drafts copy the existing values for every unrequested field, including weekdays,
checklist text and reminder minutes. Explicit empty collections replace their stored values;
an explicit null reminder disables reminders. Model responses must include every required
constructor field, including nullable reminders, so omission requests a correction rather than a clear.
Approval preserves checklist checked state when its text and order are unchanged, and preserves
the full reminder configuration when the draft retains an existing reminder. An unchanged cadence
retains its interval, recurrence anchor and cutoff. Validation, conflict checks and single or combined
previews use the same typed adjustment arguments. Adjustments preserve the existing cadence and clock interval unless a change was requested.
When a required cadence or clock interval is absent, the model proposes minimal valid values
from account context. Those are editable proposals, not supplied facts. Start and end times
remain required; no response field becomes optional.

Skipped habit reads retain their dated skip facts, including active skip receipts for
recurring habits. They exclude skipped entries from completed and remaining totals.
`HabitListCardItem` appends optional nullable boolean `isSkippedOnDate`. A dated query
emits true for a skipped row and false for other rows; an undated query omits it.
Its date is the enclosing habit list's resolved account-local `date`, including after
refresh crosses midnight. Existing `status` values remain unchanged; a skipped row
retains `status: "none"`. A habit list containing skipped rows requires optional
`clientContext.supportsSkippedHabitRows: true`, in addition to the existing parts,
kind and dated-list capabilities. Omission, null or false keeps the localized typed
text fallback, including on stored replay. Refresh also requires that capability.
Clients that declare support receive the query-bound `habitList` first, with no
repeated prose. Stored replay retains the field, and missing-capability text uses
it to preserve the localized skip state. Stored titles remain verbatim. Deploy
the API before the paired web and Android consumer renders the optional field.
The request-only `AgentClientContext.supportsSkippedHabitRows` OpenAPI property is
`{ "type": ["null", "boolean"] }` and is absent from `required`; its default is null.
Consumers declare it only when they render `isSkippedOnDate` against the list's date.
Legacy clients without parts keep their readable path; unsupported kinds use typed fallbacks.
The parts and family kill flags still apply. No contract field changes type or optionality.
For a complete filtered habit read with zero total rows, the empty block retains
`read.scope.search` and its existing screen action. One short contextual text part names
that searched value: `No habit matches “piano”.` or `Nenhum hábito encontrado para “piano”.`
This essential scope explanation comes only from typed query metadata, with no metrics
fabrication or additional model call. Nonempty, partial, failed and unfiltered reads
do not receive that explanation. Legacy unavailable-streak text remains supported.
The API regression fixtures replay #26's executed tool arguments at the model-service port;
they do not claim raw provider output or correct erroneous calls by rewriting them. Tests
verify unchanged tool availability, faithful dispatch, typed dependency selection, incomplete
plan continuation, declared-complete empty termination and later-round terminal reads.
The partial-list fixture has 51 habits, so limit 100 truthfully produces complete data and
limit 50 produces partial data with an authenticated next cursor. The tool description
keeps the normal page size at 50 for an all-habits request unless the person explicitly
requests another page size. An all-habits request does not silently enlarge that page.
Completeness still follows returned rows and total count, rather than a consumer's
three-row preview. #1822 owns the disconnected-calendar stale
expectation. #26 owns the live bilingual rerun before activation.

Each write tool declares the required boolean `orbit_write_complete` argument. The model
sets it to true only when the round covers every requested change, including dates, times
and bounded preparation items. A remaining lookup or dependent write declares false. The
server strips the argument before validation, execution, previews and persistence. It ends
the write loop when every declared call ran, every call is a write with a populated pending
preview or a coalesced preview, and at least one pending preview remains. Incomplete or
failed writes retain continuation. Completion uses structured tool calls and typed preview
results, never patterns in the person's message.

### Restart plans

Restart requests use the existing `query_habits` read with `date: "today"`,
`is_completed: false` and `include_overdue: true`. The model gathers any remaining
pages before proposing a complete plan. The model chooses a smaller set for today
and later dates for the rest from those results and the person's preferences.
No message pattern routes this behavior.

`bulk_reschedule_habits` exposes optional `revised_items` for individual dates:

```json
{
  "revised_items": [
    { "habit_id": "owned habit UUID", "updates": { "due_date": "YYYY-MM-DD" } }
  ],
  "orbit_write_complete": true
}
```

The model includes every selected habit, including today's choices whose date may
stay unchanged. It omits the shared `filter` and `due_date` when using this shape.
The original shared-date shape remains supported. Stored revisions retain their
existing precedence: `revised_items` overrides a shared filter/date.

The response uses one existing `pendingOperations` entry with action
`rescheduleHabits`, complete `items`, editable `due_date` fields and
`previewFingerprint`. Item dates distinguish today's smaller plan from deferred
habits. Unchanged proposed dates remain in `items[].fields`; `changes` contains
actual differences only and is not a complete rendering source. There is no new
block kind, strict enum value, client capability or response field. Composition
returns the preview alone, with no separate prerequisite read blocks or repeated
figures. Existing legacy preview negotiation remains available.

Approve, edit and reject use the existing pending-operation routes. An edit
preserves every unmentioned item and field. Rejection changes no habits. Approval
executes the stored per-habit dates through `BulkUpdateHabitsCommand`, its existing
FluentValidation validator and the domain's update guards. It preserves cadence,
titles, times, reminders, checklists and historical logs. Rescheduling retains the
app's existing due-date/recurrence semantics; this is not a history rewrite.

The paired web and Android ticket must render all preview item dates, retain the
existing controls and add real-call en and pt-BR cases in `evals/astra`, including
approval with independent persisted readback and pass^3. Those consumer evals are
owned by the coordinator. Local structured model-port replays do not establish
real-call pass^3. Deploy the API before the paired consumer. Tracks
thomasluizon/orbit-tickets#1855.

Registered tools and awaiting previews remain available to the model regardless of message
wording or date count. The prompt distinguishes a fresh request from a correction; the
model selects creation or `revise_pending_changes` and supplies the target preview and edits.
Revisions preserve unnamed fields and items. Suggestion refinements retain the model's
validated cadence and explicit days; the server does not infer a replacement schedule from
instruction keywords.

The model receives account-local today, yesterday and tomorrow on each turn. Earlier
previews do not define the current date. Explicit event dates remain explicit, and
preparation offsets use the event date. Pending preview context uses snake_case field
names, including item_id, entity_id and entity_name, and identifies the operation.
Revision instructions distinguish discarded item IDs from retained items and occurrence
skips from removal of a proposed change. These are model instructions, not message routing
or server inference of the person's intended target. The live bilingual gate verifies
the model's selection; local replays verify prompt inputs and typed preview composition.

Skip previews include a noneditable action row, localized as "Skip" or
"Pular", alongside the user's local occurrence date. Eligible skip operations
can share one atomic approval with other account changes. Skips with active streak-goal
side effects remain outside atomic bundles. A partial mutation result aborts the entire bundle,
rolling back every member write and confirmation consumption so the approval remains available
for retry. Existing-parent wrappers in structured creation arguments contribute their children
under the resolved parent rather than duplicate parents. A single-create wrapper with several
children becomes a bulk creation so the preview and execution retain every sibling.

`pendingOperations`, `actions` and `operations` remain compatible. #1620 renders the existing
preview/outcome before read parts and reports each operation once. Preview, edit, reject,
partial results and uncertain execution retain the existing operation gateway. Repeated
pending operation IDs with the same outcome deduplicate. A suggestion creates a chat request;
a write still needs its approval preview. Proactive notification delivery keeps its separate
contract; its action enters this chat pipeline.

Clients without parts support receive readable `aiMessage` text for pending writes even
when narrative is blank or rejected by grounding. The server uses the typed preview's entity
names and a localized approval instruction, never an execution claim or internal operation
identifier. This fallback also covers parent-preview recovery, current previews on block-answer
replay, and the parts kill flag. It is independent of `astra_block_first`; disabling composition
must preserve readable legacy answers. Stream reset and replacement text agree with the final
response and recorded history. A pending preview keeps contextual follow-ups suppressed.

Daily Astra allowance exhaustion retains HTTP 403 with `errorCode: PAY_GATE` and the same
code on SSE errors. Its localized reason states the allowance limit without an upgrade offer.
Account language selects the initial copy,
and HTTP/SSE localization uses internal copy arguments to preserve the quota reason.
Those arguments are excluded from the wire response. Other Pro gates retain their copy.

Injection refusals include the brief offer of normal habit help required by the security rule.
Other refusals and plan navigation stay within the requested scope. Composition preserves the
model's concise answer rather than deleting a help sentence with a text pattern.

## Brain dump approval

Tracks thomasluizon/orbit-tickets#1856. An unstructured typed or transcribed message
uses the existing `bulk_create_habits` command for one combined pending preview.
The model resolves existing activities through account reads and supplies `habit_id`
for reuse, preserving unnamed fields. `suggest_habits` supplies editable drafts when
the person also requests ideas; their `existingHabitId` maps to `habit_id` in the
combined proposal. Finish account and suggestion reads in earlier rounds before
submitting the complete proposal. Reused items carry the stored title verbatim.
Chosen drafts retain their title, emoji, cadence, weekdays, local date and time
interval, checklist and reminder settings. Null reminder offsets map to disabled
reminders with an empty offset array; empty days and checklists remain empty arrays.
The model must not invent a quantity, rename a draft or replace its activity.
No message pattern routes or triggers a tool.

Bulk creation preview `items` append optional nullable string `habitType`: `Task`
for one-time items, `Habit` for recurring or flexible items, and `General` for
unscheduled general items. The server derives it from structured schedule fields
and the stored schedule for reused items whose cadence is unchanged. Absence or
null means unknown. This adds no value to an existing strict enum. Other preview
operations may omit it. Revision and refresh recompute the type from the proposal;
stored chat replay preserves it.
Children retain the command's parent schedule inheritance unless explicitly one-time.
Checklist placement omits the type because it adds a checklist item rather than a habit.

The paired web and Android renderer groups the existing preview items by this field
inside one preview, preserving item IDs and order within each group. Localized group
labels are Tasks / Tarefas, Habits / Hábitos, and General / Geral. Unknown or absent
types retain the existing ungrouped display. Group headings are presentation only;
they never create parent habits or alter item placement. An `entityId` identifies a
reused item; its existing field options and old/new values retain the adjustment
controls. Render each pending operation once, using its existing approve, edit and
reject actions. No new block kind, endpoint or capability flag is required. Existing
clients ignore the additional field and keep the ordinary approval preview.

The reply contains at most one short sentence without figures, then the preview.
One held `bulk_create_habits` preview suppresses supporting read blocks in both
ordinary and block-first composition, including empty habit lists, profile rows,
calendars and suggestion cards. The existing server suppression omits redundant
narrative and follow-ups. A bulk creation submitted in a round with reads is deferred:
its typed tool result has `status: awaiting_reads`, no preview is held and nothing
is saved. The model receives those read results before submitting the complete
bulk creation in a later round. The deferred creation remains an outstanding obligation
until a later `orbit_write_complete` bulk creation yields a nonempty approval preview.
Supporting reads cannot satisfy it. If continuation ends empty, fails or stops without
that proposal, the turn returns the existing failure instead of a read-only answer.
Empty-output failures use `AI_UNAVAILABLE` so generic empty-answer recovery cannot
turn the unfinished proposal into success. Other provider failures retain their codes.
Other write tools retain their existing ordering.
Nothing is saved until approval. Edits use the existing pending-operation revision route;
reject cancels the whole pending operation. Existing FluentValidation validators
and domain guards remain authoritative for every create and adjustment.

Deploy the API before the paired consumer reads `habitType`. The coordinator files
the paired UI ticket and real-call cases in `orbit-ui-mobile/evals/astra` from this
contract. Cover en and pt-BR dumps, including an existing habit, with three consecutive
real-call successes and persisted readback after approval. The local scripted model
replays in `tests/Orbit.Application.Tests/Fixtures/BrainDumps` are deterministic
coverage and do not substitute for that real-call gate.
The added model instructions and proposed consumer labels are reported in
[BrainDumpCopy.md](BrainDumpCopy.md) in en and pt-BR for coordinator copy approval.

## Append-only follow-up context

`followUps` remains an optional string array. Parsing accepts zero, one or two questions,
reduces larger sets to two, and rejects duplicates, URLs, directives and lines over 60
characters. Composition screens current-turn grounding and read relevance. Failed tools,
truncation, pending approval and clarification suppress suggestions. No extra model round runs.

Clients may append `supportsFollowUpContext: true`. Applicable supported reads also produce
optional `followUpContext` entries, preserving the legacy question:

```json
{
  "label": "Show my habits?",
  "question": "Show my habits?",
  "context": "query_habits",
  "target": "habits",
  "queryId": "11111111-1111-1111-1111-111111111111"
}
```

Labels are at most 40 characters, shortened with an ellipsis. `question` is complete;
`context`, `target` and `queryId` come from the successful read and existing screen. Domain
guards and FluentValidation bound text and targets. Unsupported or irrelevant context is omitted.

## Streams, storage and verification

Composition withholds model answer deltas. Started, round and tool-step feedback remain.
Final SSE and synchronous delivery use the same response. Existing block-answer replay stores
that response. Recording additionally stores the final serialized response in encrypted
`ConversationTurns.ResponseJson`, under the existing opt-in, retention and size limits.
Legacy assistant text remains available. History never proves execution or grounds facts.
Replay renegotiates stored blocks against current client capabilities and family kill flags;
unsupported kinds use typed readable fallbacks. FORMAT lives in the dynamic prompt so the
shared static prefix remains request invariant.

Model-authored JSON envelopes are narrative input, never a source of blocks, actions or screen
links. Before directive parsing and grounding, the server extracts string text from `parts`
or `aiMessage`, including JSON fences. It discards model-supplied blocks and actions. Invalid
or truncated composition envelopes contribute no text; existing truncation or unavailable
handling supplies the truthful status when no typed result can answer. Extracted text still
passes the same short-line and grounding rules. Final responses and recordings contain the
resulting parts rather than an envelope serialized into a text part.

`AnswerCompositionTests` drives real assembly and the production read factory across read
families, legacy/missing-kind negotiation, misplaced/omitted directives, duplicate prose,
compound requests, previews, short text, follow-ups, SSE and serialization replay. Domain and
infrastructure tests cover guards, prompts and encryption. Broad activation requires #26's
English and Portuguese real-pipeline cases; local tests do not claim live corpus evidence.

`astra_answer_composed` records fixed family, leading kind, external-text count, fallback,
build failure and duplicate suppression. `astra_composition_follow_ups` includes zero counts.
Neither captures prompt/completion text or arguments. Model-call telemetry and spending limits
remain unchanged. The policy preserves data-backed answers, approval for every write and
single reporting without changing server-owned model selection or quotas.

## One small starting step

Tracks thomasluizon/orbit-tickets#1857. `suggest_breakdown` appends optional
`one_small_step` (boolean, default false) and `language` (`en` or `pt-BR`).
When `one_small_step` is true, `title` is the activity and
`suggested_sub_habits` contains exactly one object whose `title` is the concrete
starting step. Both texts must be nonempty, single-line and at most 200 characters.
The model schema requires a nonempty `suggested_sub_habits` array with a required,
nonempty single-line `title` on each item. Ordinary breakdowns still allow multiple
suggestions, and execution preserves the legacy ordinary empty-array behavior.
An isolated failed one-step call returns its error to the model for one correction
in the same turn. That correction must contain only one `suggest_breakdown` call
with `one_small_step: true`; it cannot introduce a write, another tool or a mode
change. A successful correction replaces the failed outcome before composition.
Provider errors, invalid corrections and exhausted budgets retain the existing
failure response. Write corrections keep their prepared-intent checks.
The model chooses the tool and the smaller action through its structured arguments;
message patterns never select or rewrite tools. Omission preserves the existing
breakdown suggestion contract and action result.

Successful one-step results end a round containing only those results, without
another model call. The response has no framing prose and one typed part:

```json
{
  "type": "block",
  "kind": "oneSmallStep",
  "block": {
    "suggestion": { "goal": "Study", "step": "Open the book", "language": "en" },
    "actions": [
      { "id": "smaller", "label": "Smaller", "message": "Give me an even smaller starting step for \"Study\". The previous step was \"Open the book\"." },
      { "id": "saveTask", "label": "Save as task", "message": "Save \"Open the book\" as a task." },
      { "id": "saveChecklistItem", "label": "Save as checklist item", "message": "Add \"Open the book\" to a habit's checklist." }
    ]
  }
}
```

Clients advertise `supportsParts: true` and include `oneSmallStep` in `partKinds`.
The kind is a string extension, without adding a value to any strict enum.
The parts kill flag still applies. Fresh replies and stored-answer replay negotiate
the kind against the client capabilities. Unsupported clients receive readable text
containing only the step, without action requests or an unsupported block. Both
ordinary and block-first composition support the contract. SSE final and synchronous
responses agree. No new endpoint, account mutation or minimum client version applies.

Actions submit their complete `message` as a fresh chat request with the current
conversation history. They are requests, never confirmation tokens or executable
commands. The smaller request contains the original activity and preceding step,
so the model can return an easier action even with incomplete history. Titles in
action messages are JSON-quoted as untrusted data.
The save request uses the existing `create_habit` tool for a one-time task or
`update_checklist` for an owned habit. A checklist target must be resolved first,
asking which habit when unclear. For an addition, send `habit_id` and
`item_to_add` containing only the step, without `checklist_items`. The server
previews the stored list plus that unchecked item and stores only the append
intent. Approval uses the existing `AppendChecklistItemCommand`, validator and
domain guard, preserving stored text, order and checked state. The derived full
list is not editable in this preview; ordinary full-list replacements retain their
existing editing support. A stale preview still requires a fresh approval.

`checklist_items` remains available for full replacements. If the supplied list
grows, its existing prefix must match the stored list exactly. The argument check
runs before policy can hold a preview and again in tool execution. The captured
pt-BR r3 addition that unchecked a stored item is rejected without a pending
operation. Adding items and changing existing ones require separate operations.
No message pattern selects a tool, rewrites its arguments or supplies an answer.
The existing pending-operation preview supports approval, editing and rejection;
no suggestion or action click alone writes a record. All ordinary ownership,
validation, policy and preview checks still run. Internal risk labels are absent
from the step block.

Deploy the API before the paired web and Android clients advertise this kind.
The paired consumer is thomasluizon/orbit-tickets#1866; en and pt-BR real-call
`evals/astra` cases are tracked in thomasluizon/orbit-tickets#1867. Required live evidence is three consecutive
passes per case and saved-step readback. Local structured-output replay is not
real-call pass^3 evidence.

### Copy for coordinator approval

`goal` and `step` are model-authored in the person's language. The fixed UI copy
and action templates are below; `{goal}` and `{step}` stand for JSON-quoted text.

| Purpose | en | pt-BR |
| --- | --- | --- |
| Smaller label | Smaller | Menor ainda |
| Task label | Save as task | Salvar como tarefa |
| Checklist label | Save as checklist item | Salvar no checklist |
| Smaller request | Give me an even smaller starting step for {goal}. The previous step was {step}. | Quero um passo ainda menor para {goal}. O passo anterior foi {step}. |
| Task request | Save {step} as a task. | Quero salvar {step} como tarefa. |
| Checklist request | Add {step} to a habit's checklist. | Quero adicionar {step} ao checklist de um hábito. |

The model-facing instructions are English at runtime, matching the existing
prompt convention. Their pt-BR versions below are for copy review.

| Source | en | pt-BR |
| --- | --- | --- |
| GlobalRulesSection | STARTING AN ACTIVITY: When the person cannot get started, use suggest_breakdown with one_small_step=true and language en or pt-BR. Return exactly one concrete, immediately doable action in suggested_sub_habits, not a plan. If they ask for something smaller, reduce the previous step's effort while keeping the same activity. The server supplies the oneSmallStep block and smaller/save actions; emit no prose. Do not call ask or propose a write unless they explicitly request saving. Save as a task through create_habit, or resolve the owned habit and use update_checklist with item_to_add containing only the new step; the server preserves all stored checklist items. Every save uses the existing approval preview. | COMEÇAR UMA ATIVIDADE: Quando a pessoa não conseguir começar, use suggest_breakdown com one_small_step=true e language en ou pt-BR. Retorne exatamente uma ação concreta que ela possa realizar agora em suggested_sub_habits, sem montar um plano. Se ela pedir algo menor, reduza o esforço do passo anterior e mantenha a mesma atividade. O servidor fornece o bloco oneSmallStep e as ações de diminuir e salvar; não acrescente texto. Não chame ask nem proponha alterações sem um pedido explícito para salvar. Para salvar como tarefa, use create_habit. Para uma checklist, identifique o hábito da pessoa e use update_checklist com item_to_add contendo apenas o novo passo; o servidor preserva todos os itens salvos. Todo salvamento passa pela prévia de aprovação existente. |
| SuggestBreakdownTool.Description | Suggest sub-habits without creating them. When the person cannot get themselves to do an activity, is stuck starting it, or asks for one concrete first step, call suggest_breakdown with one_small_step=true even if they do not say start. Set language to en or pt-BR matching their message, title to the activity and suggested_sub_habits to exactly one concrete starting action. Never return an empty array. For a request to make a step smaller, return an easier action requiring less effort than the previous step, keeping the same activity. The server returns oneSmallStep with smaller and save actions. Emit no prose or lists alongside it. Suggestions never save anything. Only an explicit save request uses create_habit for a Task or update_checklist, with item_to_add containing only the step, resolving the owned habit first. The server preserves all stored checklist items. Both require the existing approval preview. | Sugira sub-hábitos sem criá-los. Quando a pessoa não conseguir se animar a fazer uma atividade, travar na hora de começar ou pedir um primeiro passo concreto, chame suggest_breakdown com one_small_step=true, mesmo que ela não use a palavra começar. Defina language como en ou pt-BR conforme a mensagem, title como a atividade e suggested_sub_habits com exatamente uma ação concreta para começar. Nunca retorne uma lista vazia. Se ela pedir um passo menor, retorne uma ação mais fácil, que exija menos esforço que a anterior, mantendo a mesma atividade. O servidor retorna oneSmallStep com ações para diminuir e salvar. Não acrescente texto nem listas. Sugestões não salvam nada. Somente um pedido explícito para salvar usa create_habit para uma tarefa ou update_checklist, com item_to_add contendo apenas o passo, identificando primeiro o hábito da pessoa. O servidor preserva todos os itens salvos do checklist. Ambos exigem a prévia de aprovação existente. |
| SuggestHabitsTool.Description change | Help starting an activity uses suggest_breakdown with one_small_step=true instead. | A ajuda para começar uma atividade usa suggest_breakdown com one_small_step=true. |
| one_small_step schema description | Return exactly one starting step, or a smaller refinement, as a oneSmallStep block. Default false. | Retorne exatamente um passo para começar, ou uma versão menor, em um bloco oneSmallStep. O padrão é false. |
| language schema description | Required with one_small_step=true; the person's message language. | Obrigatório com one_small_step=true; idioma da mensagem da pessoa. |
| Argument error | one_small_step must be a boolean. | one_small_step deve ser um booleano. |
| Argument error | Return exactly one nonempty starting action and goal, each at most 200 characters on one line, with language en or pt-BR. | Retorne exatamente uma ação inicial e uma atividade, ambas preenchidas, cada uma em uma única linha com até 200 caracteres, com language en ou pt-BR. |
| Domain guard | A starting step requires a goal, a single short action and a supported language. | Um passo inicial exige uma atividade, uma única ação curta e um idioma aceito. |

The checklist append changes add the following model-facing copy. Runtime
instructions remain English; pt-BR translations are supplied for copy approval.

| Source | en | pt-BR |
| --- | --- | --- |
| UpdateChecklistTool.Description | Update an owned habit's checklist. To add a starting step or another item, send only item_to_add; the server appends it unchecked and preserves every stored item, its text, order and checked state. Use checklist_items for a full replacement instead. A full list that adds items must preserve the existing list as an unchanged prefix. Never send both fields. | Atualize o checklist de um hábito da pessoa. Para adicionar um passo inicial ou outro item, envie apenas item_to_add; o servidor o acrescenta desmarcado e preserva todos os itens salvos, com o mesmo texto, ordem e estado de marcação. Para substituir a lista inteira, use checklist_items. Ao adicionar itens por uma lista completa, mantenha todos os itens existentes no início, sem alterações. Nunca envie os dois campos. |
| item_to_add description | One item to append unchecked. Omit checklist_items; the server keeps all stored items unchanged. | Um item para acrescentar desmarcado. Omita checklist_items; o servidor mantém todos os itens salvos sem alterações. |
| checklist_items description | Full desired list for replacement. If adding items, preserve all existing items unchanged and in order. Omit when using item_to_add. | Lista completa para substituição. Ao adicionar itens, preserve todos os itens existentes sem alterações e na mesma ordem. Omita ao usar item_to_add. |
| Argument error | Send item_to_add or checklist_items, never both. | Envie item_to_add ou checklist_items, nunca os dois. |
| Argument error | item_to_add must be a string. | item_to_add deve ser um texto. |
| Addition guard | Adding checklist items must preserve every stored item's text, order and checked state. Send only item_to_add to append one item safely. | Ao adicionar itens ao checklist, preserve o texto, a ordem e o estado de marcação de todos os itens salvos. Envie apenas item_to_add para acrescentar um item com segurança. |

## Capability fallback and feature-help copy review

Root habit creation is checked against the configured account capacity before
`create_habit` or `bulk_create_habits` can hold an approval preview. The check
counts new roots across the full call, including explicit new-parent placements.
Existing activities and children do not consume new root slots. Save commands
retain their locked capacity checks to handle changes after preview preparation.

`HABIT_LIMIT_REACHED` returns the existing localized error and its formatting
arguments as a typed failure. The model explains it through ordinary continuation;
the failure cannot offer approval or become a generic retry message. Grounding
accepts the returned capacity as a refusal fact without treating it as a saved
habit. No message keywords route this behavior.

| Source | en | pt-BR |
| --- | --- | --- |
| Existing `HABIT_LIMIT_REACHED` copy, reused | You've reached the {0} habit limit. | Você chegou ao limite de {0} hábitos. |
| `TerminalWriteDirective` addition | If a write returns HABIT_LIMIT_REACHED, explain the returned limit in the person's language. It applies to every plan; do not propose upgrading, deleting data or bypassing it. | Se uma alteração retornar HABIT_LIMIT_REACHED, explique o limite retornado no idioma da pessoa. Ele vale para todos os planos; não proponha mudar de plano, excluir dados ou contornar o limite. |

The instruction is English at runtime; its Portuguese version is for copy review.
The error copy is unchanged in both languages. No fixed answer text is added.

The following server copy is retained verbatim and moved into the localization catalog.
The model instruction is shared by the tool description and its result. Its Portuguese
translation is for copy review; the model receives the English instruction in both locales.

| Source | en | pt-BR |
| --- | --- | --- |
| `chat.answer.pendingPreview` | Review the proposed changes before approving. | Revise as alterações propostas antes de aprovar. |
| `chat.answer.empty` | I couldn't prepare an answer. Please try again. | Não consegui preparar uma resposta. Tente novamente. |
| `chat.answer.toolFailure` | I couldn't complete that. Please try again. | Não consegui concluir isso. Tente novamente. |
| `chat.answer.truncated` | This response was cut off before completion, so the result is partial. | Esta resposta foi interrompida antes do fim e pode estar incompleta. |
| `chat.answer.selectHabit` | Which habit would you like to choose? | Qual hábito você quer escolher? |
| `chat.answer.selectHabitReference` | Which habit would you like to use for “{0}”? | Qual hábito você quer usar para “{0}”? |
| Removed scope heading | All habits; Remaining habits; Habits on this date | Todos os hábitos; Hábitos restantes; Hábitos nesta data |
| Removed calendar title prefix | Habit calendar: | Calendário de hábitos: |
| `DescribeFeatureTool.Description` | Read authoritative rules before explaining streaks, freezes, frequencies, XP/levels, notifications, paygate or schedule math. Use only mechanics, screens, fields and steps explicitly documented in markdown. related_surfaces lists destinations, not editing instructions. When the requested detail is absent, say that the explanation does not cover it; do not invent navigation or settings. Answer in the person's language. | Consulte as regras oficiais antes de explicar sequências, proteções de sequência, frequências, XP e níveis, notificações, limites do plano ou regras de agendamento. Use apenas os mecanismos, telas, campos e passos descritos no markdown. related_surfaces lista destinos, sem explicar como editar. Se o detalhe pedido não estiver no conteúdo, diga que a explicação não cobre esse ponto. Não invente caminhos de navegação nem configurações. Responda no idioma da pessoa. |
| `describe_feature.answer_scope` | Use only mechanics, screens, fields and steps explicitly documented in markdown. related_surfaces lists destinations, not editing instructions. When the requested detail is absent, say that the explanation does not cover it; do not invent navigation or settings. Answer in the person's language. | Use apenas os mecanismos, telas, campos e passos descritos no markdown. related_surfaces lista destinos, sem explicar como editar. Se o detalhe pedido não estiver no conteúdo, diga que a explicação não cobre esse ponto. Não invente caminhos de navegação nem configurações. Responda no idioma da pessoa. |
