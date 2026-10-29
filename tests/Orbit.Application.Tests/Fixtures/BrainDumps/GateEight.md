# Captured brain dump plans

The `g8-*.json` fixtures project the real Gate 8 attempt evidence from staging
API commit `1e7707b6`: case ID, repeat, tool calls, successful tool results and
the corresponding input from `orbit-ui-mobile`'s composition scenarios.
The tool arguments are preserved verbatim, including explicit nulls and completion
directives. Redacted habit IDs are rebound to the test's owned habit. Suggestion
IDs omitted by the evidence projection are supplied locally; draft content comes
from the captured result. Profile data is deserialized against the API's own type;
calendar reads use the production query handlers against the test repository.

The model-service port replays independent reads before the captured complete
write. The incomplete reuse attempt preserves the mixed round demonstrated by its
tool timings. That replay checks that the model receives the read before any
preview is held. No replay infers intent from a message pattern.

These unit replays cover preview composition, identity, schedules and wire replay.
They do not claim real-call pass^3 or live persistence evidence. The paired eval
ticket and coordinator own that verification.
