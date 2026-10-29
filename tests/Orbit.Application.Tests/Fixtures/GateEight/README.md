# Gate 8 model output

These captures retain the tool names, arguments and result statuses from the real
`#26` staging run against API `1e7707b6`, attached to
[the work order](https://github.com/thomasluizon/orbit-tickets/issues/1674).
The source is `eval-gate7-g8b/evals/astra/reports/attempts/<case>-r<n>.json` in the
coordinator evidence. Only the case identity and `toolCalls` are copied. Account
identifiers remain redacted. Replays replace a redacted target with the matching
test account identifier; they preserve all other tool arguments.

`composition-avoid-form` repeats 1 and 3 in both languages combine
`is_bad_habit: true` with `is_general: true` and omit recurrence. Repeat 2 carries
Day/1 and `is_general: false`. Tests reject the former before previewing and retain
the latter in one approval preview. Recovery pairs the failed repeat 1 with the
same language's real repeat 2 output at the model-service port; this demonstrates
the continuation contract, not a fresh measurement of model reliability.
