# AI turn plans

Tracks thomasluizon/orbit-tickets#1976. Publish the API before the paired
web and Android consumer adds its shared Zod schemas and renderer.

Chat responses, conversation display turns and stream events append optional
`plan`. Its fields are `id` (UUID), `title` and ordered `steps`. Each step has
`id`, `text`, `status`, `tool` and `phase`, with optional `toolCallId` and
`pendingOperationId`. The tool identifier and phase bind progress to execution;
render only the model-authored title and text. Status is `pending`, `running`,
`done`, `failed` or `skipped`. Phase is `execution` or `approval`.

The model calls `present_plan` alongside the first action tools in the same
response. The plan covers at least two action calls, or a write preview plus
approval. A single read and a plain text reply have no plan. The control call
does not dispatch an account operation, count as a composition read, or add
a model round. A control call without action calls cannot cause a continuation.
Invalid or repeated plans do not replace the accepted plan or trigger actions.
Plan titles and step text follow the conversation language, use plain words,
contain no internal tool or habit type names and fit within 40 characters.
The plan has at least two steps. There is no fixed fallback copy.

The stream publishes `{"type":"plan","plan":{...}}` before dispatching the
first action. Changes use
`{"type":"plan_step","planId":"<UUID>","planStep":{...}}`.
Replace the matching step by ID, including updated call and preview bindings.
The final chat response contains the complete plan with the latest statuses.
Concurrent reads can finish in either order; a completed read publishes its
status while other reads remain running. Steps never execute account actions.

For writes, the execution row describes preparing the preview. The approval
row remains pending and carries the actual pending-operation ID. Combined
previews rebind their approval rows to the combined pending operation. Approval
makes these rows running; execution success makes them done, and a denial or
failure makes them failed. Approval alone never means done. A recorded rejection
or expiry outcome makes its approval rows skipped. Steps that were not reached
when the turn ends become skipped, while rows awaiting approval remain pending.

The existing approval endpoints are ordinary HTTP responses after the chat
stream has closed. Confirmation, rejection through preview revision, and
execution append the updated `plan` to their respective responses. Apply that
block to the original plan by its ID. Approval does not open another model
turn or stream. An active chat stream continues to use `plan_step` events.
When conversation recording is disabled, the plan remains available for the
current streamed turn and the client can reconcile its preview bindings from
the existing typed approval and execution outcomes.

The complete plan is stored inside the assistant turn's encrypted
`ResponseJson`. Preview transitions patch that JSON in the same protected
transaction as their existing outcomes. Execution writes its actual terminal
status. Conversation reload exposes the same plan as an optional typed turn
field, preserving `responseJson` and every existing field. No migration is
required. The `astra_conversation_recording` gate is rechecked for recording
and updates; when off, no plan is stored. Deleting the conversation deletes
its plan with the rest of the turn.

Plan response updates are auxiliary after a committed approval, rejection or
execution. A failed update logs an error with `PendingOperationId` and preserves
the primary response; request cancellation still propagates. Confirmation and
rejection already patch the stored plan atomically with their pending-state
changes through `PendingAgentOperationStore.SaveChangesWithConversationOutcomes`.
Their subsequent response update must not invalidate those commits.

Execution cannot universally share that transaction: `ExecuteWithPolicyAsync`
uses a shared transaction only for transactional tools and account change sets;
other tools dispatch commands with their own save boundaries or external effects.
The executor records the actual terminal outcome in the existing agent audit
store with source `pending_operation_execution` and the pending operation ID as
`TargetId`, then attempts the auxiliary plan update. The outcome contains no
conversation text and uses no model call. Audit persistence retains its existing
independent scope and error reporting. If both the execution audit and the plan
write are unavailable, the primary response is still preserved and both failures
are observable; reload leaves the execution unresolved rather than inferring
success from a consumed confirmation.

Conversation display and model-history reads repair unfinished approval steps
from the owner's pending state and explicit execution outcome. Approval alone
stays running. A consumed confirmation without an execution outcome never proves
success or rejection, including after its approval window expires. A failed execution remains failed even when its confirmation was consumed.
Reconfirmation excludes older execution outcomes. Repair is bounded to the loaded
page, takes the same user write lock and rechecks `astra_conversation_recording`
before writing encrypted plan JSON. Disabling recording prevents repair writes.
No schema migration or additional configuration is required.

Old clients may ignore the additional fields and unknown stream event types.
Old payloads without plans still deserialize. No client capability is needed
to receive the additive fields, and existing parts and action blocks retain
their composition and approval behavior.

Offline cost evidence uses the `o200k_base` tokenizer and SDK-serialized
requests and function calls from the same local transport as the #1916 cache checks. The baseline
removes only the plan declaration and instruction. A representative two-tool
turn adds 290 input tokens and 64 output tokens, conservatively including the
function-call envelopes. With the existing warm static prefix, 47,806 input
tokens (46,976 cached) and 121 output tokens cost 0.00063401 USD under the
configured prices, below the 0.0009 USD warm-call
ceiling. Cold requests retain #1916's separate cold-start bounds. Reproduce with
`dotnet test tests/Orbit.Infrastructure.Tests --filter FullyQualifiedName~Plan_TwoToolTokenDeltaFitsExistingWarmCallCeiling`.
The measurement writes `orbit-1976-plan-cost.txt` in the process temporary
directory and makes no paid provider call.
