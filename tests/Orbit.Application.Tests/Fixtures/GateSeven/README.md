# Compound request replay evidence

Tracks thomasluizon/orbit-tickets#1674. Captures come from #26 gate 7 on staging
API `2b44af74`, under `eval-gate7/evals/astra/reports/attempts` in the coordinator's
read-only run scratchpad. Case IDs, repeats, reasons, rendered answers and executed
tool calls are copied without correcting their names, arguments or titles.

Tests inject these calls as the internal `AiResponse` at `IAiIntentService`, using
the real handler, pending store, previewer and operation executor. Redacted IDs
are bound to owned fixture entities. Recorded dates stay unchanged and the date
service returns the captured account-local day. The rename-plus-skip test approves
the combined preview and asserts both dispatched commands and the skip occurrence.
The Portuguese skips retain their typed action labels and cannot become logs or deletes.

The medical replay preserves the captured titles, polarity and schedule fields, including a past event date.
The Portuguese capture contains the model's misspelled activity title: the replay
intentionally preserves that mistake. Prompt guidance, rather than message rewriting,
corrects future generation. Passing this replay does not prove the live model improved.

Suggestion captures expose rendered application drafts, not raw provider envelopes.
They replay at `IHabitDraftGenerator`, preserving their typed field values. Redacted
IDs are rebound and localized rendered dates are parsed back to the same calendar date.
The regression verifies draft identity, faithful projection and the update approval path.
The captured invented scheduling remains visible; the generator instructions are the fix.

Prompt-contract tests were strengthened and observed failing before the instruction
change. Only the coordinator's real-call gate can measure whether these generation
instructions meet pass^3; deterministic captured-output replay cannot prove that claim.
