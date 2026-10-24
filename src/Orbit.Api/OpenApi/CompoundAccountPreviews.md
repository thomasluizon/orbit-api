# Compound account previews

Tracks thomasluizon/orbit-tickets#1674. Build on the existing pending-operation
confirm, revise and execute routes and the `fieldOptions` contract documented in
`ExistingParentPreviews.md`.

A chat turn with several held writes returns one pending operation with action key
`applyAccountChanges`. Its stored operation is `apply_account_changes`. Every member
keeps its own command, ownership checks, policy and validation. The combined
operation requires the strongest confirmation required by its members and executes
inside one transaction. A failed member rolls back the request.

The preview contains every member's items. An item's ID prefixes its original ID
with the member index, for example `0/0` or `1/<habit ID>`. Submit the returned IDs
and fingerprint through the existing revise route. Include the items to retain;
omitting an item removes it from the proposal. A revision returns its current item
IDs and fingerprint. Use those values for the next revision or approval.

Corrections through `revise_pending_changes` patch the existing preview ID. The tool
keeps every item and field unless the person explicitly changes or removes it.
The combined operation reserves its member proposals, including members removed
from its visible items. Members cannot be confirmed separately. Rejecting the
combined preview cancels those reservations; executing it consumes them. Neither
operation leaves an independent proposal available for a later approval.

Habit creation accepts parent IDs, nested children through the supported habit
depth, checklists, descriptions, emoji, schedule, start and due dates, times, end
dates, reminders, slip alerts, tags and goal IDs. Habit and goal creation and edits
use application commands. Initial weekday dates use the same schedule calculation
for the preview and creation command.

An exact existing habit match is reused through its ID. Multiple exact matches
require an existing-habit choice inside `fieldOptions`. A suggested existing parent
has an optional parent choice that includes No parent; an ambiguous named parent
still requires a choice. These choices are validated again before execution.

Normal write replies contain the preview without model narration or follow-ups.
Server-authored unresolved-name questions and continuation-budget notices remain
available when the request needs them. The model receives pending previews for
corrections, and an older pending proposal does not block a fresh conversation.

The change adds no endpoint or database migration. Deploy the API before consumer
support for `applyAccountChanges` and the optional field choices. Authentication,
session and consent operations marked as direct client flows retain that policy.
Real-model evaluation and model-cost decisions remain with the consumer eval
corpus under its separate work order.
