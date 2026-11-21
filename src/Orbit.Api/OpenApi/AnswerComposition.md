# AI answer composition

This file is the producer contract for chat composition. It states the wire shape and the rules that a client can depend on. The model-facing strings live in code, in `HabitModelInstructions.cs`, `AskTool.cs`, `SuggestBreakdownTool.cs` and `ErrorCopy.cs`. The date contract is in [AiDateContext.md](AiDateContext.md).

## Contents

- Capability negotiation
- Read composition
- Family to block table
- Ask forms
- Habit lists and metrics
- Writes and previews
- Restart plans
- Brain dump approval
- One small starting step
- Follow-up context
- Proactive offers
- Streams, storage and telemetry
- Provider requests and truncation

## Capability negotiation

A client sends these optional fields in `clientContext`. Omission or `false` keeps the readable text path.

| Field | Use |
| --- | --- |
| `supportsParts` | The client renders `parts` of `ChatResponse`. |
| `partKinds` | The block kinds that the client renders. An empty list still gets server composition with readable text. |
| `supportsSkippedHabitRows` | The client renders `isSkippedOnDate` against the date of the list. |
| `supportsStartingStepContext` | The client copies `previousStep` to `previousStartingStep`. |
| `supportsFollowUpContext` | The client reads `followUpContext`. |
| `supportsProactiveOffers` | The client renders one next action. See "Proactive offers". |
| `supportsPendingOperationChanges` | The client renders field changes in a preview. |
| `supportsMessageAllowance` | The client reads `messageAllowanceConsumed`. |
| `conversationId` | The identity of the stored conversation. |

The server selects the tool catalog from these fields. It never uses the words of a message. A kind that is not in `partKinds` gets readable text. A replay of a stored answer negotiates again against the current capabilities and flags.

The database flag `astra_block_first` is off at seed. When it is on, the server composes blocks for a client that sends `supportsParts`. When it is off, the server uses negotiated directive assembly. A client with proactive offers always gets server composition. A kill flag that is on disables its capability:

| Flag | Capability |
| --- | --- |
| `astra_parts_disabled` | all parts |
| `astra_ask_blocks_disabled` | `choice`, `pickList`, `question` |
| `astra_follow_ups_disabled` | `followUps`, `followUpContext` |
| `astra_period_blocks_disabled` | period blocks, metrics, rankings |
| `astra_goal_projections_disabled` | goal projection fields |
| `astra_status_blocks_disabled` | `daySummary`, `streak`, `calendar`, `gamification`, `habitMonth` |
| `astra_record_lists_disabled`, `astra_account_rows_disabled` | `recordList`, `accountRows` |
| `astra_change_preview_disabled`, `astra_tool_steps_disabled` | change previews, tool steps |

Moderation replies and chat errors use `clientContext.locale`, or else the profile language. A legacy client gets HTTP 400 with `CONTENT_MODERATION_REFUSED`. A client with `supportsMessageAllowance` gets a normal response with `messageAllowanceConsumed: false`.

## Read composition

A read tool declares the required boolean `orbit_read_complete`. The value is `true` when the round fully answers the request. It is `false` for a prerequisite lookup and when a read or a write remains. The server removes the argument before it runs the tool.

The server skips the next model call when all of these are true:

- Every call declares `true`, and every call ran.
- Every tool is read-only, and every result succeeded with a typed projection.
- No clarification, write or pending operation remains.

Then the server composes the answer from the typed results. Any other case keeps the tool loop. The server never invents narrative and never runs an unapproved write.

- Blocks come from successful typed results. A model directive cannot create a block.
- Reads render in the recorded order. One goal list renders for each answer. Equivalent reads merge. Different entity IDs, date windows and metrics stay separate.
- A lookup is a prerequisite when its entity IDs first appear in that result and feed a later round.
- A complete empty habit, goal or record list keeps its typed block with `items: []`, `total: 0` and complete metadata.
- A client without the block gets readable text from the same result. The day label is `Today` or `Hoje` when the date is the account day from `IUserDateService.GetUserTodayAsync`.
- Model-written JSON envelopes are narrative input only. The server discards model blocks and actions.

When `RequireApiKeyCreationStepUp` is on, `ApiKeyManagementAuthorization` raises the `get_api_keys` read to a `PendingConfirmation` operation with `StepUp` and action key `viewApiKeys`. The chat emits no `recordList`.

`get_daily_summary` with `generate_narrative: true` composes one sentence beside the `daySummary` block. The model returns `habitId` and `summary` for the first actionable pending step. The server drops the sentence when the ID differs, the JSON is not valid or the sentence has digits.

## Family to block table

| Family | First surface |
| --- | --- |
| Habits and schedules | Query-bound `habitList` or `daySummary` |
| Logs and history | `recordList` with the `habitHistory` subtype |
| Metrics, streaks, level and achievements | Focused `metrics` or `streak` |
| Rankings | `habitRanking` |
| Totals and comparisons | `periodTotal` or `periodComparison` |
| Goals | Query-bound `goalList` |
| Calendar and sync | `calendar`, with the real recovery state |
| Notifications, tags, templates and keys | The relevant `recordList` subtype |
| Profile, settings, plan and referrals | The relevant `accountRows` subtype |
| Retrospectives | Focused `periodInsight` |
| Editable habit suggestions | `habitSuggestions`, with no prose around it |
| Writes and outcomes | The pending preview or the action outcome |
| Clarification and choices | `choice` or `pickList`; the question lives in the block |
| Proactive chat turns | The read block, or a short no-data nudge |
| Empty, partial and stale | The relevant block, with its completeness and recovery |
| Failure, quota, plan, offline and refusal | The typed terminal outcome or a short status |
| Help, coaching and small talk | One to three lines of at most 180 characters, one sentence per line |

`StreakCard` has the optional `firstEligibleDate`, the local date of the earliest creation of an active habit. It is absent when no active habit exists.

## Ask forms

The server declares `ask` only for JWT clients that advertise parts and one of `choice`, `pickList` or `question`, when the kill flags allow it. Other clients get readable clarification text.

- The schema requires an `input` object with one form. `choice` needs a question and options. `pickList` needs a title, an action and items. `question` needs `questions`.
- Every `ask.input` form requires `missing_input`: `habit_setup`, `preference`, `check_in`, `record_selection` or `bulk_selection`.
- A `pickList` item for `create_habits` has `arguments` as a JSON string of one `bulk_create_habits` item. For `log_habits` and `skip_habits` it is a `{habit_id, date}` object.
- The initial request never declares `ask`. A successful typed account read, or a typed create result for a missing field, unlocks it. A create result binds `draft_call_id` and `missing_field`.
- Record selection and the log and skip `pickList` actions need a successful `query_habits` with `for_selection=true` earlier in the turn.
- An accepted `ask` ends the reply. An invalid `ask` returns the failed constraint to the model, which can retry once. An `ask` cannot end a round with unfinished account reads.
- A singular name with duplicate titles needs one fresh selection read and one `choice` before a write. A full-set request uses `for_selection=false`.

The answer to a `choice` carries the selected option IDs and labels as `AiChoiceSelection`. A selection never approves a write.

A bound habit-setup block stores an `AiDraftClarification`. The server merges the answered field into the stored draft, and the create tool prepares the normal preview. An absent or expired draft returns the expired-block error before it charges allowance.

When composition and an ask kind are on, a tool-free final answer is a JSON envelope with `aiMessage` and `kind`. Only `kind: "answer"` is complete. Any other envelope gets one correction in the same turn.

## Habit lists and metrics

`HabitListCardItem` has the optional nullable boolean `isSkippedOnDate`. A dated query sets it to `true` for a skipped row and `false` for other rows. An undated query omits it. A skipped row keeps `status: "none"`, and skipped entries do not count as completed or remaining.

A list with skipped rows needs `supportsSkippedHabitRows: true` and the parts, kind and dated-list capabilities. Without it, the client gets localized text. In OpenAPI, `AgentClientContext.supportsSkippedHabitRows` is `{ "type": ["null", "boolean"] }` and is not required.

The completion series of habit metrics appears only when its scheduled-completion total equals the period log count.

## Writes and previews

A write tool declares the required boolean `orbit_write_complete`. The model sets it to `true` only when the round covers every requested change. The server removes it before validation. The write loop ends when every call ran, every call is a write with a pending or coalesced preview, and one preview remains at least.

- `pendingOperations`, `actions` and `operations` stay compatible. The client renders each pending operation once.
- The server combines the held writes of a turn into one preview. It never adds a write that the person did not request.
- A write denied with `target_not_owned` beside a prepared preview gets one correction with owned IDs. A failed correction withdraws the previews of the turn. The answer is `chat.answer.requestNotPrepared`, or `chat.answer.requestNotPreparedEarlierKept` when an earlier preview stays.
- Eligible skips can share one atomic approval with other changes. A partial result rolls back every write.
- `HABIT_LIMIT_REACHED` returns a typed failure. The server composes the limit and the next step, a sub-habit under an owned habit. It prepares no preview.
- Daily allowance exhaustion returns HTTP 403 with `errorCode: PAY_GATE`, and the same code on SSE errors.
- A client without parts gets readable `aiMessage` text for a pending write. The text never claims an execution.
- `suggest_habits.count` is a required integer from 1 to 5. Other values return `HABIT_SUGGESTION_INVALID`.
- `update_checklist` takes `item_to_add` or `checklist_items`, never both. `item_to_add` appends one unchecked item through `AppendChecklistItemCommand`. A full list that adds items keeps the stored list as an unchanged prefix.

## Restart plans

A restart request reads `query_habits` with `date: "today"`, `is_completed: false`, `include_overdue: true`, `for_selection: false` and `orbit_read_complete: false`. The model reads every page and then sends one `bulk_reschedule_habits` call:

```json
{
  "restart_plan": true,
  "revised_items": [
    { "habit_id": "owned habit UUID", "updates": { "due_date": "YYYY-MM-DD" } }
  ],
  "orbit_write_complete": true
}
```

`restart_plan` is a required boolean: `true` for a restart, `false` for an ordinary reschedule. `revised_items` replaces the shared `filter` and `due_date`.

The server checks a declared restart before dispatch:

- The same turn has a successful `query_habits` for the complete due and overdue set. The pages share one source revision.
- `revised_items` matches that set exactly, has unique IDs and changes only `due_date`.
- Each date is from local today to seven days later.
- Strictly fewer habits stay today than the full set.

A refusal names the absent and extra habits and stages no preview. A valid single restart preview completes the turn, also when `orbit_write_complete` is `false`.

The response is one `pendingOperations` entry with action `rescheduleHabits`, complete `items`, editable `due_date` fields and `previewFingerprint`. `changes` holds only real differences.

## Brain dump approval

An unstructured message uses `bulk_create_habits` for one combined pending preview. The model resolves owned activities with `query_habits` and sends `habit_id` for reuse. `suggest_habits` supplies drafts when the person asks for ideas, and `existingHabitId` maps to `habit_id`. A reused item keeps the stored title and sends only the requested fields.

`suggest_habits.excluded_activities` is an optional list of titles that the preview already plans.

- A supplied list, including `[]`, asks for new activities outside the list and the account. Omission or null keeps ordinary suggestions and adjustments.
- The list holds at most 100 titles. Each title has a letter or a digit and at most 200 characters.
- The server rejects a draft with a listed `existingHabitId` or title. A rejected draft gets one correction. A repeated failure returns `HABIT_SUGGESTION_UNAVAILABLE`. Invalid input returns `HABIT_SUGGESTION_INVALID`.

The server defers a `bulk_create_habits` call in a round with reads. Its result has `status: awaiting_reads`, no preview and no saved data. The model sends the complete call in a later round. A turn that ends without a nonempty preview returns a failure, and an empty output uses `AI_UNAVAILABLE`.

Bulk preview `items` have the optional nullable string `habitType`. `Task` is for one-time items. `Habit` is for recurring or flexible items. `General` is for one-time tasks with no date. The server derives it from the schedule. Null means unknown. `entityId` identifies a reused item.

One held bulk preview removes the read blocks. A combined preview after `suggest_habits` holds each issued draft once, under its title. The server rejects an absent, renamed, duplicate or invalid draft.

## One small starting step

`suggest_breakdown` has the optional `one_small_step` (boolean, default `false`) and `language` (`en` or `pt-BR`). With `one_small_step: true`, `title` is the activity. `suggested_sub_habits` holds exactly one item whose `title` is the starting action. Both texts are nonempty, on one line, and at most 200 characters. A failed call gets one correction in the same turn.

A successful result ends a round that holds only such results. The response has no prose and one part:

```json
{
  "type": "block",
  "kind": "oneSmallStep",
  "block": {
    "suggestion": { "goal": "Study", "step": "Open the book", "language": "en" },
    "actions": [
      { "id": "smaller", "label": "Smaller", "message": "Give me an even smaller starting step for \"Study\". The previous step was \"Open the book\".", "previousStep": { "goal": "Study", "step": "Open the book", "language": "en" } },
      { "id": "saveTask", "label": "Save as task", "message": "Save \"Open the book\" as a task." },
      { "id": "saveChecklistItem", "label": "Save as checklist item", "message": "Add \"Open the book\" to a habit's checklist." }
    ]
  }
}
```

The client advertises `supportsParts: true` and `oneSmallStep` in `partKinds`. Without `supportsStartingStepContext: true`, each action has only `id`, `label` and `message`. An unsupported client gets text with only the step.

An action sends its `message` as a new chat request. An action is never a confirmation. For `smaller`, a client with `supportsStartingStepContext` copies `previousStep` to `clientContext.previousStartingStep` (`goal`, `step`, `language`). A malformed value returns the HTTP 400 client-context error. The server adds the value to the dynamic context as untrusted data.

A save request uses `create_habit` for a one-time task, or `update_checklist` with `item_to_add` for an owned habit. Every save uses the normal approval preview.

## Follow-up context

`followUps` is an optional array of two questions at most. A client with `supportsFollowUpContext: true` gets `followUpContext` entries for supported reads:

```json
{
  "label": "Show my habits?",
  "question": "Show my habits?",
  "context": "query_habits",
  "target": "habits",
  "queryId": "11111111-1111-1111-1111-111111111111"
}
```

A label has at most 40 characters. `context`, `target` and `queryId` come from the successful read.

## Proactive offers

A client turns on offers with `clientContext.supportsProactiveOffers: true`, `supportsParts: true`, `supportsPendingOperationChanges: true` and a nonempty `conversationId` UUID. Omission keeps the prompts, schemas and read path unchanged.

For these clients, the create and edit schemas of habits, goals and checklist templates add the optional `suggested_fields`. It lists the fields whose values are optional offers. Requested fields stay out of it. An invalid name, shape or value returns a repairable failure that names the field.

The first offer turn carries `offer_account_facts`, bounded habit, goal and tag indexes. Each offer turn carries `offer_suggestion_candidates`.

`PendingOperationChange` has the optional `provenance` (`requested`, `stored` or `suggested`) and `removal` (`offerKey`, `field`, optional `fields`). The revision endpoint accepts `items[].removeSuggestedFields`, and `revise_pending_changes` accepts `items[].remove_suggested_fields`. A removal restores the earlier configuration.

Read tools of offer clients declare `orbit_offer_intent`: `aim`, `dated_event`, `inspect` or `none`. A preview next action uses a tool-free selector with a three-second deadline. A selector failure keeps the preview.

A validated action appears in `read.nextAction`, or in `nextAction` of a pending preview. It has `offerKey`, `kind` and `label`, and the optional `surface`, `prompt`, `entityId` and `date`. The kinds are `openSurface`, `continueChat` and `preparePreview`. No kind approves a write.

To select a chat action, the client sends its `offerKey` as `clientContext.selectedOfferKey` in the same conversation. The server loads the issued action by key, owner and conversation. It refreshes the bound query and needs the same revision. A stale or dismissed action returns `stale_offer` and starts nothing. A successful selection consumes the offer.

`POST /api/chat/offers/dismiss` accepts `{ "conversationId": "<uuid>", "offerKeys": ["<semantic-key>"] }` and returns 204. It takes one to 100 keys of at most 200 characters each. `clientContext.dismissedOfferKeys` reports the same event on the next turn, and the model can report `orbit_dismissed_offer_keys`. The server accepts only keys that it issued to the same account and conversation.

The table `ConversationOffers` stores one row for each account, conversation and semantic offer key. A dismissal survives the reissue of the offer. The server encrypts each definition. A conversation delete, an account reset and an account deletion remove the rows. Account export includes them.

## Streams, storage and telemetry

Composition withholds model answer deltas. Final SSE and synchronous delivery use the same response.

When `astra_conversation_recording` is on, `ChatResponse.conversationId` identifies the stored thread. The server writes the negotiated parts and compact tool results to the encrypted `ConversationTurns` table in `ResponseJson`. A request with a known conversation ID uses the stored history and ignores client history. [ServerConversations.md](ServerConversations.md) holds pagination, images, deletion, retention and export.

## Provider requests and truncation

A habit tree has at most five levels with the root. A six-level call returns validation feedback and prepares no preview.

An `Incomplete` `max_output_tokens` round with no output usage and no text gets one retry with the same input. A run of 257 whitespace characters in function-call arguments cancels the stream and uses the same one retry. A second failure stays truncated. The server never parses partial arguments.
