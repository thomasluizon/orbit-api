# Captured honesty tool plans

These tool arguments come from the Gate 6 failing-case rerun, harness `be8ab21e9`
and staging API `43886600`. They retain the trace producer's `name`, `arguments`
and `resultStatus` fields without inventing a provider wire response.

`ambiguity-en-r1` retains the incomplete choice question, which the server preserves;
the prompt owns asking for both the target and the change. `missing-query` retains
only the `query_habits` call from `grounding-missing-pt-r2`, including its false
completion flag. `edge-skipped-pt-r1` retains the dated habit lookup.

The replay boundary is `IAiIntentService`, whose `AiResponse` and `AiToolCall`
contracts belong to this repository. Tool results are produced by the real tools
and operation executor. These are tool-plan replays, not raw provider HTTP captures.
The original attempts and the fixture reset script are the coordinator's read-only
`run-26-g6rerun` evidence. Re-derive the captures from the named attempt JSON files
by selecting their `toolCalls` arrays, filtering the missing case to `query_habits`.

The additional complete clarification cases construct `AiResponse` at the same
repository-owned port. They model the expected structured output in both languages;
they are not provider captures or evidence of live model adherence. The terminal
empty-lookup cases set the captured call's completion flag to true and assert the honest
no-data composition in both languages. The compound nonterminal case replays the single
captured prerequisite lookup with its false completion flag and an empty repository
result, then asserts continuation into an issued `ask` block for creation details. Neither
tool selection nor response composition depends on matching the person's message.

Stored option labels and habit titles stay verbatim. Locale rules apply to generated
questions and status messages.

`unsupported-question-r1`, `r2` and `r3` retain the subsequent rerun's three `ambiguity-en`
tool plans against staging API `82fc57f2`. The model supplied both requested inputs inside
an unsupported question form. Replays verify capability negotiation to the existing choice
form, preserving the supplied question, options and free-text permission without continuation
or a write. Re-derive them from `eval-g6rerun` attempt files by retaining `caseId`, `repeat`,
`reason`, `answer` and `toolCalls`.

`composition-pickList-pt-BR-r1`, `r2` and `r3` retain the failed `ask` calls from
the Gate 6 rerun on staging API `cd9f6401`, harness `ad302ad51`. Re-derive them
from the identically named `g6rerun-reports/attempts` files by retaining `caseId`,
`repeat`, `reason`, `answer` and `toolCalls`, filtering calls to `ask`. All three
calls supplied a question-only `intro` on a pick list. None revised a pending
preview. The successful prerequisite query in r2 is excluded to isolate the
failed selection at the model-service port.

The reports redact record IDs. Replays bind item IDs and nested habit IDs to
entities created by the real domain factory, retaining the captured dates and
other arguments. The English variants translate only the supplied display text;
they are synthetic bilingual coverage, not live English model evidence. Replays
exercise the real ask tool, validator, domain guard and handler composition,
assert a stored typed selection and verify that only argument checks run, with
no preview, write or model continuation. Question-form introductions remain
covered by their existing round-trip test.
