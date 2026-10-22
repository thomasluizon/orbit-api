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
results. Other successful reads render in recorded operation order, including independent reads
alongside a pending preview or write outcome. Rankings use the complete count result and retain
their produced position. Only equivalent query families, kinds and canonical scopes deduplicate;
different entity IDs, date windows and metrics remain distinct, including history scopes.
The server neither queries an implicit dashboard nor derives numbers from prose.

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
| Writes and outcomes | Existing pending preview or action outcome, without repeated prose | #24, #1620 |
| Clarification and choices | `choice` or `pickList`; question lives in the block | #1620 |
| Proactive chat turns | Corresponding read block or short no-data nudge | #1620 |
| Empty, partial and stale | Relevant block retains completeness and supported recovery | #1619, #1620 |
| Failure, quota, plan, offline and refusal | Existing typed terminal outcome or concise status | #1620 |
| Help, coaching and small talk | One to three lines, at most 180 characters each, no headings or unsolicited bullets | #1620 |

Supported data blocks have no external prose by default. One short status may explain
truncation or failure alongside successful data. Missing capabilities receive readable text
from the typed result. Builder failure never becomes an empty success. Successful typed reads
survive narrative outage. Empty lists carry an existing screen action in `relatedSurfaces`
when applicable. Unknown directives are stripped. Model text and follow-ups retain grounding.
Deterministic fallback text is already grounded by its typed producer.

`pendingOperations`, `actions` and `operations` remain compatible. #1620 renders the existing
preview/outcome before read parts and reports each operation once. Preview, edit, reject,
partial results and uncertain execution retain the existing operation gateway. Repeated
pending operation IDs with the same outcome deduplicate. A suggestion creates a chat request;
a write still needs its approval preview. Proactive notification delivery keeps its separate
contract; its action enters this chat pipeline.

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
