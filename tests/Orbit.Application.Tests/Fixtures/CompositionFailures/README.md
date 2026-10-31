# Composition regression evidence

The `prod-r9b-` fixtures retain rendered answers, executed tool arguments and the
typed results used by the regressions from production API `63a6a33b`, harness
`1c8210815`. Sources are the coordinator's read-only
`eval-gate-prod-r9b/evals/astra/reports/attempts/<case>-r<n>.json` files. These are
internal model-service-port replays, not raw provider responses. Daily-summary
narratives are the recorded typed tool result. Redacted IDs, dates and times in
suggestion results are replaced explicitly by deterministic test values; the
original capture stays unchanged. Question recovery uses the passing captured
read and choice plan in separate rounds with owned test IDs. It does not claim
that the live model already emits that correction.

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

## Typed draft and shared-title captures

The `prod-r7-` fixtures retain every failing attempt from production API `eb2c4cc2`
and harness `cc06309d4`: case ID, repeat, reason, rendered answer, tool calls and
request phase timing. Rendered answers are evidence, never model-authored text.
The interrupted choice attempt contains no calls; it records one model phase of
65,660.862266 ms and no per-model output or reasoning token counts.

Replays reject the captured pre-read setup calls even when their proposed title is
punctuation, and check the absence of ask from the initial model-service request.
The create-draft replay obtains missing fields from the real CreateHabitTool producer,
then checks the continuation declaration and rejection of a different draft or field.
Duplicate-choice replays reuse the captured query and ask, replace only redacted IDs,
and omit the refused write from the candidate corrected model output. They pin typed
context and write guidance and retain the separate guard tests for captured writes.
The smaller-step replay uses the single-contact baseline documented in the work order
and pins the exact guidance sent beside it. These tests verify contracts and delivery;
the coordinator's real-model probe establishes tool choice and semantic effort reduction.
