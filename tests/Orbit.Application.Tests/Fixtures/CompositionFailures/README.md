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

The `prod-r4` fixtures retain case IDs, repeat numbers, rendered answers and executed
tool-call evidence from production API `f890b5d2`, harness `22c556d2d`. The source is
`eval-gate-prod-r4/evals/astra/reports/attempts/<case>-r<n>.json` in the coordinator's
read-only run scratchpad. Help replays use the captured text as the narrative response;
proactive replays preserve the emitted reasons and question forms, including invalid calls.

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

The `g8-` captures come from gate 8 on API `1e7707b6`, under
`eval-gate7-g8b/evals/astra/reports/attempts/` in the coordinator's read-only scratchpad.
Case IDs, repeats, rendered answers and executed tool calls are copied unchanged. The captured
legacy creation uses a complete bulk write; compound reads use habits or a habit month plus
goals. Tests replay these plans through production tools and composition, with deterministic
repository ports and approval previews. They also negotiate serialized responses for replay.
The captured feature-help plan runs the real description producer; tests inspect its payload
at the narrative-model port. Its recorded answer is consumer-rendered evidence and is never
injected as model text. The stub narrative verifies delivery, not live model compliance.

The `g8r2-` compound capture comes from gate 8's filtered rerun on API `83f0f154`,
under `eval-gate7-g8r2/evals/astra/reports/attempts/` in the coordinator's read-only
scratchpad. The case ID, repeat, rendered answer and tool calls are copied unchanged.
Its daily-summary call remains a daily-summary read in the test. Deterministic query
ports return the typed summary facts; a synthetic directive-only continuation exercises
composition because the captured calls do not declare read completion. The rendered
answer is evidence only. Local replay verifies formatting, synchronous and streamed
composition, recording and replay; it does not prove a live model now chooses a habit
listing. The coordinator owns that bilingual gate rerun.

## Production rerun ask captures

The `prod-r1-composition-compound-legacy-*` fixtures retain the answer and complete
tool-call arguments from the #26 production rerun on API `9ba7dfea`, harness
`6cae53206`. They preserve the captured empty ask followed by habit and goal reads.
The replay asserts the actual model-service declarations, grounded fallback text,
stored composition and absence of ask execution for legacy clients.

The production streak fixture retains the original habit creation timestamps and typed
read arguments. Its replay checks account-local eligibility, ignored completed and deleted
habits, empty eligibility, stream withholding and persisted composition. The feature-help
fixture retains the original describe_feature call; its narrative replay checks full lines
from the real explanation and surface catalog rather than repeating the captured truncation.
