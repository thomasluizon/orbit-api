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

## Medical argument audit

The prompts and required arguments are in `evals/astra/composition-scenarios.yaml`
on `orbit-ui-mobile` `origin/main`, read as evidence only. The event dates in these
captures are October 26 at 15:50 and October 30 at 13:30, in the account's year.
Preparation continuing until the second event starts on October 27 for the
multivitamin instruction and October 29 for Muay Thai, with daily recurrence and
inclusive October 30 end dates. Fasting ends on October 30 at 13:30.

| Capture | Argument defects compared with the requested actions |
| --- | --- |
| en r1 | Both preparation commitments are OneTime instead of bounded daily items. The multivitamin title substitutes "Don't take" for "stop". |
| en r2 | Dates, times and recurrence match. "Stop taking multivitamins" expands the person's "stop multivitamins". |
| en r3 | Muay Thai is OneTime instead of bounded daily. The multivitamin title substitutes "Don't take" for "stop". |
| pt-BR r1 | The preparation starts are swapped: multivitamin on October 29, Muay Thai on October 27. "Parar com multivitamínico" keeps the person's wording with an accent correction. |
| pt-BR r2 | Muay Thai is OneTime instead of bounded daily. Fasting is on October 29 and omits the 13:30 deadline. "Parar de tomar" expands "parar com". |
| pt-BR r3 | Both preparation commitments are OneTime. Multivitamin starts on October 29 instead of 27, Muay Thai on October 30 instead of 29. "Suspender" substitutes for "parar com". |

Both exam dates, exam times and OneTime frequencies are correct in all six
captures. Every temporary commitment has `is_bad_habit: false`. The exact title
rubric belongs to `#1822`; no canonical replacement title is imposed here.
The medical tests retain the captured mistakes in typed previews rather than
silently repairing model arguments. They also verify that the model receives the
offset, deadline and title guidance. The coordinator's fresh real-call gate
measures whether the model follows it.

## Skip lookups

`compound-update-skip-en-r3` omits `skip_habit` after two lookups and a rename.
`voice-pt-r2` stops after its lookup. Both read a skipped occurrence. The matching
successful captures, en r1 and pt-BR r3, contain explicit skip calls despite that
read state. Tests replay lookups and subsequent writes as separate model rounds,
substituting only the account IDs. The real query tool produces the skipped
readback from typed schedule facts; its payload is not mocked. Assertions cover
the one combined preview and the absence of invented writes for omitted calls.
