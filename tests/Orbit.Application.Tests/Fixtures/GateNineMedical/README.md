# Medical preparation captures

These are the unmodified `caseId`, `repeat` and `toolCalls` projections from
`eval-gate7-g9a/evals/astra/reports/attempts/<case>-r<n>.json` in the coordinator's
gate 9a evidence, routed through
[the work order](https://github.com/thomasluizon/orbit-tickets/issues/1674) from
[the harness findings](https://github.com/thomasluizon/orbit-ui-mobile/pull/2200).
The run header identifies harness `fba2380cc` and API `74c6e4f51`.
The tool arguments contain no account identifiers. The Portuguese first repeat
called `ask` and is outside this preparation-argument regression.

English repeats 1 and 3 and Portuguese repeat 2 emit the 24-hour restriction as
OneTime with no end date. Portuguese repeat 2 also places fasting on the previous
day without its collection deadline. English repeat 2 and Portuguese repeat 3
contain bounded Day/1 restrictions and the collection-day fasting deadline.

The replay enters through `IAiIntentService`, uses the real chat handler, policy,
pending-operation store and previewer, then approves the held operation. Only the
MediatR dispatch is substituted; assertions inspect typed create commands and the
bulk result. Empty link arrays in the English captures use individual create
commands; null link fields use the bulk command. Both retain one held preview.
Both mistaken and correct arguments remain visible without repair.
Two additional variants shift the successful captures' machine date fields and
description references by 64 days before replay, crossing the year boundary.
That test-only transformation is
not a model capture or a production argument rewrite. Its request dates and
account-relative today shift together.

The strengthened prompt assertions fail on the base implementation because its
system and tool descriptions lack the explicit 24-hour and deadline-only field
mapping. No new structural validation is added: the existing create contract
contains no typed preparation-to-event relationship from which to prove a bound.
Local replay proves argument preservation and the instructions supplied to the
model. It does not prove live model adherence or pass^3; the coordinator owns the
real-call rerun.

The `g9i-` projections retain the coordinator's `caseId`, `repeat` and `toolCalls`
from `run-26-gate7-g9i/reports/attempts`, harness `df6cdae61`, API `29d83553`.
Portuguese repeats include the correct offsets, swapped offsets with a verb
substitution, and an extra preparation item made from the first offset. Replays
keep those arguments unchanged in typed previews and inspect the system and tool
instructions supplied at the model-service port. They preserve the person's
unpunctuated message verbatim. They do not establish that the model follows the
offset guidance; the coordinator owns that live rerun.

The English batch supplies the correction's real pending preview; the correction
capture contains two identical time edits with completion false then true.
Replay binds the redacted pending ID to that preview. The unmodified sequence
stops at the complete second call. A labeled test variant changes only the first
completion flag to true, proving that one complete revision ends the turn. A
separate structured replay keeps completion false to retain a requested fresh
write after the revision. Revision-question tests cover the answer path even
when the model marks its read complete. No provider response fields are added.
