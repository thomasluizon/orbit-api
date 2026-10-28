# Composition regression evidence

These are unmodified case IDs, rendered answers and executed tool-call evidence from
#26's gate 6 failing-case rerun on API `43886600`, harness `be8ab21e9`.
The source is `eval-g6rerun/evals/astra/reports/attempts/<case>-r<n>.json`
in the coordinator's read-only run scratchpad. Timing and grader assertions are
omitted because the deterministic tests do not consume them.

The files contain executed tool arguments, not raw Responses API output. Tests replay
those arguments as the internal `AiResponse` tool plan at the model-service port;
they do not invent provider message items, call IDs or response envelopes. Synthetic
IDs used by the test producer replace redacted account IDs where necessary.
`Fixtures/TerminalReads` separately covers the real provider/SDK response boundary.

Captured plans are replayed without correcting their tool names. Wrong-family calls remain
their actual typed results or negotiated readable fallbacks. Corrected structured plans are
separate tests; only a subsequent model call can request a dependent read. Fixtures do not
authorize message keyword routing, fixed advice or hiding tools from the model.

The empty, notification, API-key, missing-habit and single-read captures come from the
rerun on API `82fc57f2`, harness `e09b7f13f`. Their tool arguments replay at the same internal
port. Their `answer` is the harness-rendered visible answer, not captured model prose; tests
never inject it as provider output. Model-authored envelope tests construct their own internal
`AiResponse` text to exercise parsing, grounding, final parts, recording and replay.

The partial fixture seeds 51 habits. Failed attempts request limit 100; passing
attempts request limit 50. Complete is truthful when all 51 rows are returned.
The rendered answer shows three rows because the consumer preview samples them.
The API regression verifies both limits against the production query and factory.
No stale data is relabeled: #1822 owns the disconnected-calendar stale fixture.

The two `composition-suggestions-duplicate` captures come from gate 7 on API
`2b44af74`, under `eval-gate7/evals/astra/reports/attempts/` in the coordinator's
read-only scratchpad. The case ID, rendered answer and executed tool calls are
copied unchanged. All three repeats in each language use the same tool plan;
the fixtures retain the first. `G7SuggestionCompositionTests` replays that plan at
the internal model-service port through the production suggestion tool, query,
service and operation executor. The generator port returns deterministic typed
drafts; the test does not claim to verify model-generated schedules. The rendered
answer proves the former server-added introduction and is never replayed as model
prose. Tests cover synchronous and streamed turns, recording and replay, both flag
states, and supported, legacy, missing-kind and parts-disabled clients.
