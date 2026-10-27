# Astra question blocks

Tracks thomasluizon/orbit-tickets#1827. Deploy the API before the renderer in
thomasluizon/orbit-tickets#1828 advertises `question` in `clientContext.partKinds`.
The client must also send `supportsParts: true`. Both the ask-block and parts
kill switches apply. Other clients continue using `choice` or prose.

## Tool and response

The existing `ask` tool accepts `form: "question"`, one to ten `questions`, and
optional `title`, `intro` and `progress: { current, total }`. Current is nonnegative,
total is positive, and current cannot exceed total. Progress need not equal the
question count. Titles have at most 120 characters and intros have at most 500.

Every question has a unique nonblank `id` (at most 80 characters), `type`, and
nonblank `prompt` (at most 500 characters). Fields for other types are rejected.

| Type | Definition | Answer |
| --- | --- | --- |
| `singleChoice` | Two to ten `options`, each with unique `id` and nonblank `label` (at most 120 characters). At most one option may set `recommended: true`. | One option ID in `selectedIds`, or a custom `freeText` when allowed. |
| `multipleChoice` | Two to ten options. | One or more option IDs, custom text when allowed, or both. |
| `scale` | Required `lowLabel` and `highLabel`, each at most 120 characters. | Integer `number` from 1 to 5, inclusive. |
| `slider` | Required decimal `min`, `max`, `step` and nonblank `unit` (at most 24 characters). | Decimal `number` in range, on a step measured from min. |
| `yesNo` | No explicit options. | `boolean: true` or `boolean: false`, or custom text when allowed. |

Single choice, multiple choice and yes/no may set `allowFreeText: true`. Custom
answers must contain nonblank text of at most 500 characters. A single choice or
yes/no answer cannot mix its typed value with custom text. Sliders require max
greater than min, positive step dividing the range exactly, and bounds within
negative one billion and one billion. Decimal arithmetic preserves fractional steps.

The response uses the existing part envelope. For example, a `question` block
with no heading contains one scale:

```json
{
  "type": "block",
  "kind": "question",
  "block": {
    "blockId": "11111111-1111-4111-8111-111111111111",
    "expiresAtUtc": "<UTC timestamp>",
    "questions": [
      { "id": "energy", "type": "scale", "prompt": "How much energy do you have?", "allowFreeText": false, "lowLabel": "Low", "highLabel": "High" }
    ]
  }
}
```

The timestamp placeholder represents an absolute expiry instant. Blocks expire after 24 hours.
`title`, `intro` and `progress` are omitted when absent. An ask call ends the tool
round and issues at most one interactive block. Asking never authorizes a write.
Question text, including scale labels and slider units, uses the person's language.

## Answer on the next turn

Both chat endpoints retain multipart transport. Set the existing `blockAnswer`
form field to JSON and keep `message` nonblank. Keep the existing required
`selectedIds` field as an empty array for a question block:

```json
{
  "blockId": "11111111-1111-4111-8111-111111111111",
  "selectedIds": [],
  "answers": [{ "questionId": "energy", "number": 3 }]
}
```

Submit exactly one answer for every issued question. Unknown or repeated IDs,
missing answers, cross-type values, disallowed custom text, and off-step numbers
return `ASTRA_BLOCK_INVALID_SELECTION` before claiming the block or consuming
allowance. Top-level `freeText` and nonempty top-level `selectedIds` cannot mix
with question answers. Legacy choice and pick-list answers reject nested `answers`.

The server validates against the owned stored definition. It sends the next model
turn JSON containing `form`, `blockId`, `title`, the stored `questions`, and typed
`answers`. IDs, booleans and numbers stay data. Prompts and option labels come from
the issued definition. Answers remain subject to moderation and crisis handling.
A fresh model answer uses the existing message allowance once.

The existing ownership, invalidation, expiry, reservation, response recovery and
replay rules apply. Identical answers replay without another model call or charge;
question order and selected-option order do not affect identity. Changed typed
values cannot replay a previous response. A replay on a client without support
becomes text containing prompts, choices and numeric bounds.

All four published OpenAPI artifacts include `AstraQuestionBlock`, its nested
types, and `AstraBlockAnswer` with optional `answers`. The existing `block` object
and multipart JSON strings retain their schemas. No migration, environment
setting or version-floor change is required.
