# Failed ask recovery evidence

These executed tool arguments come from #26 gate 9b on staging API `bc1389b3`.
The source is `eval-gate7-g9b/evals/astra/reports/attempts/<case>-r<n>.json`
in the coordinator's read-only scratchpad. Only case identity and executed calls
are retained. Tests replay them as internal `AiResponse` plans at the model-service
port, with synthetic call IDs, rather than constructing provider envelopes.

The compound capture supplies the failed null question followed by its actual
`query_habits` and `query_goals` recovery calls. The replay deliberately places
the failed ask in the first response and the reads in the continuation to test
recovery independently of same-round call ordering.

The medical capture contains only the failed null question: the old handler
discarded continuation tool calls, so that report cannot establish the lost
model output. The recovery response uses the captured batch from
`Fixtures/GateEight/composition-medical-batch-pt-BR-r3.json`, without changing its
arguments. It tests whether a model-proposed batch survives a failed ask; it does
not claim to reproduce the unobserved gate 9b continuation.
