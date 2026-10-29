# Ask captures

Tracks thomasluizon/orbit-tickets#1716. These are the nine gate 8 attempts
against staging API `1e7707b6`, from `evals/astra/reports/attempts` in #26's
`eval-gate7-g8b` evidence. Each fixture preserves the original `caseId`, `repeat`
and complete `toolCalls` values, including null placeholders and result statuses.
No model arguments or results are invented. Other attempt fields are omitted.

The replay tests send each captured ask through the real tool and replay it at
`IAiIntentService`. Calls in one attempt are successive model rounds, not a
single parallel tool batch. The compound case also retains its skipped reads.

| Original attempt | SHA-256 of the complete source file |
| --- | --- |
| `ambiguity-en-r1.json` | `fb97154c080bcb300d28c03053ca46de1f7c4d36e2243f8a98e180495854afe0` |
| `ambiguity-en-r2.json` | `0a8b9a875703d6a7ffc21c729f91f19a2e50decaea28c74a680284dc37e9e9aa` |
| `ambiguity-en-r3.json` | `08830d9e5faa32831da074ba635230924f784caea637b84f53e625c35aba0244` |
| `composition-choice-en-r1.json` | `80ee0ea996d26b403e98fc3a6a60a723aaae198fc9a239966a3b3ec73df6b376` |
| `composition-choice-en-r2.json` | `34d1cf4fb4da6507997da3f4b0deecf8ab8d2c3e2e032882e7f538cee128d2bb` |
| `composition-choice-pt-BR-r1.json` | `3b986cf73499c79095aaa79ccf81ee0b5c86d67167b9ea3803c81e49de59c491` |
| `composition-choice-pt-BR-r2.json` | `334edea77b919ce32d4a8e7c4596824bd23d38e220e6294e83a9eac754e86c5e` |
| `composition-choice-pt-BR-r3.json` | `85333de356854a9b93ee03615a190e87a54b6b0baff19c2fe34b418d5ed6b201` |
| `composition-compound-missing-kind-en-r3.json` | `d216e08da6b2460cbcff4c63dbe4c26f41986ba7f2a64f3138c7c87fb0f34ada` |

The original rejection reasons are:

- All first choice-composition calls and `ambiguity-en` r1/r3 put
  `allowFreeText=true` at the top of a question form. The old definition mapper
  forwarded it to a domain flag that must be false for that form, even though
  each nested question already specifies its own free-text setting.
- `ambiguity-en` r2 and `composition-choice-en` r2 also omit the options required
  by `singleChoice`. Free text does not remove that requirement. The corrected
  second r2 ambiguity call still failed because its top-level flag stayed true.
- `composition-compound-missing-kind-en` r3 has no `questions` array and advertises
  no supported ask form. The production negotiation rejects it for capability;
  the tool itself rejects the missing array. Its captured later reads were not
  executed. The replay preserves the model's prose fallback for this client.
- The successful r3 ambiguity and r2 English composition retries render a single
  record selector as `question` when the client also advertises that form. The
  server now negotiates a validated, renderable single selector to `choice`.

The gate's separate requirement to ask what change is wanted remains in the
model instruction. Replays preserve captured wording; they never fabricate a
missing question or claim live model compliance.
