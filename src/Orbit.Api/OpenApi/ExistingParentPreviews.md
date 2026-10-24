# Existing parent previews

Tracks thomasluizon/orbit-tickets#1671. The consumer implementation belongs to
thomasluizon/orbit-tickets#1677, blocked by the API deployment. The real-call eval
case belongs to thomasluizon/orbit-tickets#1674.

Pending operation items append optional `fieldOptions`. Each entry names an editable
field, whether a choice is required, and its offered JSON values with display labels.
An ambiguous parent offers only matching owned habits and an explicit null choice.
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
heading displays `newValue: "No parent"`, `proposedValue: null` and an optional
choice (`required: false`). It can be approved immediately or changed to an offered
owned parent. An older client can reject any preview using its existing controls;
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

These turns return `pendingOperations` with no `aiMessage`, text parts, follow-ups
or streamed text deltas. Preview grounding is enforced independently of the global
`AstraGroundingEnforce` setting. No database migration or configuration change is
required. Deploy this producer before publishing the consumer that renders
`fieldOptions`.
