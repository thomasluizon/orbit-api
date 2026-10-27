# Account preview regression captures

These are the redacted tool arguments and visible answers from the gate rerun on staging API `43886600`, harness `be8ab21e9`, for thomasluizon/orbit-tickets#1674. Source files are `evals/astra/reports/attempts/<capture>.json` in the coordinator evidence bundle.

The tests replay the captured calls at the owned `IAiIntentService` boundary through the production chat handler, tool registry, previewer and pending-operation store. They do not invent an OpenAI wire response. Redacted habit and pending-operation IDs are rebound to the test account. The Portuguese parent fixture retains its Portuguese routine names. The skip occurrence date is rebound to the test user's local today.

`orbit_write_complete` is the API's chat-only round protocol, added by the tests to captured write arguments. It is declared to the model and stripped before the operation schema, command, preview or persistence sees it. The stored capture itself remains unchanged. The terminal-round test queues the captured destructive continuation and proves that completing the write round never requests it.

Creation and revision replays keep the full registered tool set and prior preview context available. Only the model's structured call selects which preview is created or revised; no test invokes a message-pattern router. Suggestion tests replay typed drafts at `IHabitDraftGenerator` and assert that normalization preserves the emitted cadence and days, including explicit weekdays, while retaining the original draft identity. These are local composition assertions; the coordinator owns the real-call bilingual gate.
