# Captured honesty tool plans

These tool arguments come from the Gate 6 failing-case rerun, harness `be8ab21e9`
and staging API `43886600`. They retain the trace producer's `name`, `arguments`
and `resultStatus` fields without inventing a provider wire response.

`ambiguity-en-r1` retains the incomplete choice question. `missing-query` retains
only the `query_habits` call from `grounding-missing-pt-r2`, including its false
completion flag. `edge-skipped-pt-r1` retains the dated habit lookup.

The replay boundary is `IAiIntentService`, whose `AiResponse` and `AiToolCall`
contracts belong to this repository. Tool results are produced by the real tools
and operation executor. These are tool-plan replays, not raw provider HTTP captures.
The original attempts and the fixture reset script are the coordinator's read-only
`run-26-g6rerun` evidence. Re-derive the captures from the named attempt JSON files
by selecting their `toolCalls` arrays, filtering the missing case to `query_habits`.

Stored option labels and habit titles stay verbatim. Locale rules apply to generated
questions and status messages.
