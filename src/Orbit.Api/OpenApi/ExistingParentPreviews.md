# Existing parent previews

Pending operation items append optional `fieldOptions`. Each entry names an editable
field, whether a choice is required, and its offered JSON values with display labels.
It appears only on an item with an unresolved parent choice, with `required: true`.
An ambiguous parent offers exactly its matching owned habits and an explicit null
choice. Resolved children and root items omit `fieldOptions` entirely.
For example, two different habits named Post Lunch produce:

```json
{
  "fieldOptions": [
    {
      "field": "parent_habit_id",
      "required": true,
      "options": [
        { "value": "11111111-1111-1111-1111-111111111111", "label": "Post Lunch" },
        { "value": "22222222-2222-2222-2222-222222222222", "label": "Post Lunch" },
        { "value": null, "label": "No parent" }
      ]
    }
  ]
}
```

The item's existing `fields` entry has `field: "parent_habit_id"`, the unresolved
heading as `newValue` and `proposedValue`, and `isEditable: true`. Its existing
`validationErrors` contains `field: "parent_habit_id"` and
`errorCode: "parent_choice_required"`. Both confirmation and execution reject an
unresolved required choice with HTTP 400 and this body:

```json
{
  "error": "parent_choice_required",
  "message": "Choose a parent habit or No parent in the preview."
}
```

Submit the selection through the existing
`POST /api/ai/pending-operations/{id}/revise` route, retaining every item that should
remain in the batch. For a one-item preview with item ID `0`, the request shape is:

```json
{
  "previewFingerprint": "the fingerprint returned by the preview",
  "items": [
    {
      "itemId": "0",
      "edits": { "parent_habit_id": "11111111-1111-1111-1111-111111111111" }
    }
  ]
}
```

Use `"parent_habit_id": null` for No parent. An unoffered value returns
`option_not_offered`. The revision returns the updated preview and fingerprint;
then use the existing confirm and execute routes. A root item with an unmatched
heading displays `newValue: "No parent"`, `proposedValue: null` and omits
`fieldOptions`. It can be approved immediately. Items without options retain their
existing editable-field and owned-parent validation. When options are present,
`parent_habit_id` can only be revised to one of the values offered on that item.
An older client can reject any preview using its existing controls;
it cannot approve an ambiguous parent by ignoring the new field.

Parent confirmation questions return only the stored operation's preview. A parent
correction builds a replacement from stored arguments, preserving every field
outside the parent selection, and cancels the original in the same transaction.
The original ID then returns the existing typed pending-operation-not-found error
on confirm or execute. A correction uses matching headings in the current message
or user history, an explicitly named unique parent, or the already bound parent.
It returns `parent_resolution_required` when none supplies the intended mapping.
Multiple pending creation batches return `pending_preview_ambiguous` rather than
selecting one without evidence.

The heading resolver retains only the candidate IDs for each unresolved item.
Preview creation, approval and revision load the referenced parent IDs and those
candidate IDs together. Stored previews without candidate metadata use a query
filtered by the unresolved title. Root-only batches do not load account habits.
Fingerprints bind item arguments and the state of their resolved or candidate
parents. Editing an unrelated habit does not invalidate a preview; editing a
bound or candidate parent does. Selecting a parent or No parent removes the
choice options from the revised item.

These turns return `pendingOperations` with no `aiMessage`, text parts, follow-ups
or streamed text deltas. Preview grounding is enforced independently of the global
`AstraGroundingEnforce` setting.

## Combined account changes

A chat turn with several held writes returns one pending operation with action key
`applyAccountChanges`. Its stored operation is `apply_account_changes`. Every member
keeps its own command, ownership checks, policy and validation. The combined
operation requires the strongest confirmation required by its members and executes
inside one transaction. A failed member rolls back the request.

The preview contains every member's items. An item's ID prefixes its original ID
with the member index, for example `0/0` or `1/<habit ID>`. Submit the returned IDs
and fingerprint through the existing revise route. Include the items to retain;
omitting an item removes it from the proposal. Each revision returns the current item
IDs and fingerprint.

Corrections through `revise_pending_changes` patch the existing preview ID. The tool
keeps every item and field unless the person explicitly changes or removes it.
The combined operation reserves its member proposals, including members removed
from its visible items. Members cannot be confirmed separately. Rejecting the
combined preview cancels those reservations; executing it consumes them. Neither
operation leaves an independent proposal available for a later approval.

Habit creation accepts parent IDs, nested children through the supported habit
depth, checklists, descriptions, emoji, schedule, start and due dates, times, end
dates, reminders, slip alerts, tags and goal IDs. Initial weekday dates use the same schedule calculation
for the preview and creation command.

An exact existing habit match is reused through its ID. Multiple exact matches
require an existing-habit choice inside `fieldOptions`. A suggested existing parent
has an optional parent choice that includes No parent; an ambiguous named parent
still requires a choice. These choices are validated again before execution.
