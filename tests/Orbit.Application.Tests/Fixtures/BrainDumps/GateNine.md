# Captured brain dump failures

These g9a attempts use API staging commit `74c6e4f5` and UI eval commit `fba2380cc`.
Each JSON retains the captured tool arguments, grounding evidence, visible answer
and approval verdict. Messages come from the corresponding
`evals/astra/composition-scenarios.yaml` case. Redacted IDs are rebound to owned
test habits; draft IDs omitted by the evidence projection are generated locally.
No other model argument is changed by the replay.

`BrainDump_GateNineCaptureKeepsIdentityAndEffectiveStartDate` replays mixed and
reuse calls at `IAiIntentService`, using production reads and preview composition.
The stored-title variants retain the captured output while exercising a stored
title whose casing differs from the proposal. `BrainDump_GateNineIdeaCaptureKeepsSuggestionAndExplicitActivities`
replays the suggestion reads and bulk proposal with the captured draft producer
output. Persistence tests approve the same captured arguments through the real
operation executor and commands and read them back from a fresh SQLite context.

The reuse calls already contain `habit_id`, and their captured approval readback
reports reuse. The UI visible-answer projection removes identity fields. The mixed
calls contain `start_date` but no `due_date`, while the visible projection also
omits `start_date`. Both limitations belong to the existing paired renderer and
eval work in thomasluizon/orbit-tickets#1863 and #1864.

The ideas r2 and r3 calls copy the suggestion producer's `HH:mm:ss` times.
Bulk validation previously accepted only `HH:mm`, so approval failed before any
new records were written. The harness labels this as duplication or a changed
fixture because its first post-approval check compares the expected row count.
The persistence replay reproduces the validation error directly.

The ideas en r1 and pt-BR r2 captures also document unnecessary calendar and
habit-month reads. Prompt and tool descriptions constrain these reads; live
tool-choice verification remains the coordinator's real-call rerun.
