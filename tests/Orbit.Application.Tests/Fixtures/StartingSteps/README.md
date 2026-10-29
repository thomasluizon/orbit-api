# Starting-step replay evidence

Tracks thomasluizon/orbit-tickets#1857. These are the 19 gate 8 attempts that
actually emitted `suggest_breakdown`, copied from the coordinator's
`eval-gate7-g8b/evals/astra/reports/attempts` evidence against API `1e7707b6`.
Case identity, repeat, tool arguments, status and visible answer stay unchanged.
Dependent cases whose setup failed before a tool call have no call to replay.

The handler tests inject the captured arguments at `IAiIntentService` and run the
real tool, operation executor, accumulator and response composition. Fourteen
captured empty arrays require correction; five successful calls require none.
The correction is a scripted model continuation, not another live-model capture.
Its actions come from successful captures in the same language; Portuguese walking
uses the fixture checklist text `Calçar os tênis`. Each continuation retains the
captured goal and language. Safety cases return writes, extra calls, a different
mode, another empty array, no call or a provider error and verify bounded failure.

The original one-step suite passed with the defect present. These new replays
failed for all fourteen empty arrays before the implementation changed. The strict
SDK wire-schema test also failed before the nonempty array constraint was added.
Live-model pass^3 verification belongs to the coordinator and the paired eval
ticket thomasluizon/orbit-tickets#1867.
