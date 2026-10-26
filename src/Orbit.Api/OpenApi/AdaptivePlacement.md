# Adaptive placement in pending previews

Tracks thomasluizon/orbit-tickets#1758. Deploy this API before the consumer in
thomasluizon/orbit-tickets#1759 advertises placement controls.

Clients opt in with `clientContext.supportsPendingOperationPlacement: true` only
when they can render the selected placement, destination, reason and alternatives,
and distinguish retained draft settings from the changes approval will execute.
`supportsParts`, `partKinds` and `supportsPendingOperationChanges` do not imply
placement support. Omitted, null or false placement support retains the legacy
creation under the displayed parent, without adaptive placement metadata or
recommendations. Model-supplied placement cannot override that default.

The consumer in thomasluizon/orbit-tickets#1759 must send this `clientContext` on
chat JSON requests and in the existing multipart `clientContext` field. Send it
also in the JSON bodies of pending preview `revise` and `execute` requests. On
`preview/refresh` and `confirm`, send the same JSON object as the URL-encoded
`clientContext` query parameter. Each request declares the capabilities of its
requesting client, including requests for a preview created on another device.

Placement-capable creation items with a resolved existing destination append
optional `placement`.
It contains `selected`, `recommended`, `reasonKey`, localized `reason` entries for
`en` and `pt-BR`, the other two `alternatives`, and `destinationId` and
`destinationName`. Values are `sub_habit`, `checklist_item` and `new_parent`.
Items without a resolved destination omit the field. An unresolved parent still
uses the existing parent choice flow. A new routine on an empty account keeps
its parent and independently tracked children by default.

The destination's checklist and active children take precedence. For an empty
destination, the account's checklist and sub-habit usage inform the recommendation.
An account with more separate root habits than either structured pattern favors a new parent.
The pattern also counts habits with neither structure, including leaves. Leaf
sub-habits do not count as evidence that the person prefers separate roots.
Ties favor checklists when checklist evidence exists, otherwise sub-habits.

The existing `POST /api/ai/pending-operations/{id}/revise` route accepts optional
`placement` on each selected item, alongside optional `edits`. Send every retained
item ID and the current `previewFingerprint`, just as for existing revisions:

```json
{
  "previewFingerprint": "<current preview fingerprint>",
  "items": [{ "itemId": "0", "placement": "checklist_item" }],
  "clientContext": { "supportsPendingOperationPlacement": true }
}
```

Switching changes the stored choice without a model call or an account mutation.
It preserves every draft setting, including schedule, reminders, tags, goal links,
checklist contents and nested activities. The original `parent_habit_id` remains
in the draft as its destination, including when the selected choice is `new_parent`.
On approval, `sub_habit` creates under that destination, `new_parent` creates at the
root, and `checklist_item` appends the proposal title to the destination's existing
checklist, preserving existing text and checked state. A checklist item follows its
destination's settings; the retained draft settings take effect if the person
switches back to an independently tracked habit before approval.

Simple English and Portuguese placement sentences use the same server revision
path before completion. The handler retains sibling items and compound operations.
A stale fingerprint, unavailable destination or invalid choice does not revise the
hold. A client without placement support cannot revise, confirm or execute a
stored non-default placement, including members of a compound preview. Revision
returns `placement_not_supported`; confirmation and execution return HTTP 400
validation errors. The operation gateway also rejects such approvals. The client
must refresh the preview with its own capabilities and show the refreshed changes
before approving. Refresh binds new arguments and a new fingerprint, invalidating
any previous confirmation token. Existing clients keep using their unchanged
confirmation and execution requests for legacy previews.

The reason keys are `chat.placement.destinationChecklist`,
`chat.placement.destinationSubHabits`, `chat.placement.accountChecklists`,
`chat.placement.accountSubHabits`, `chat.placement.accountFlat`, and
`chat.placement.newRoutine`. Copy lives in `HabitPlacementRecommendation` in both
locales. New checklist validation copy lives in `ErrorCopy` in both locales.
The coordinator owns `/second-opinion` copy approval and the owner's staging turn
for "add X to my morning routine" after deploying this head to staging.
