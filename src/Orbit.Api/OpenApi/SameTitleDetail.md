# Same-title detail in previews

Tracks thomasluizon/orbit-tickets#2161, build step 3b of thomasluizon/orbit-tickets#2129.
The web and mobile clients read the new field in build step 3-ui. Deploy this API before
that client work.

A preview item shows the title of its record. When two or more habits of the account
have one title, two items can show one name. The same-title detail tells them apart: the
title of the parent habit and the place of the record among the habits that share its
title, in the order of the app list. A record with no parent has the place only.

## Contract

Each change is append-only. `MinSupportedVersion` does not change.

1. `PendingOperationItem` appends the optional string `entityDetail`, for example
   `"Morning, 5 of 6"`. An item with no detail omits the member.
2. `AgentClientContext` appends the optional boolean `supportsPendingOperationEntityDetail`.
3. `GET /api/chat/conversations/latest` takes the optional query parameter `clientContext`:
   a JSON object in the form of `AgentClientContext`, as the preview refresh route reads it.
   A value that is not such an object returns HTTP 400.

A client that sends `supportsPendingOperationEntityDetail: true` gets the detail in
`entityDetail`, and `entityName` stays the plain title:

```json
{ "itemId": "0", "entityName": "Water", "entityDetail": "Morning, 5 of 6" }
```

A client that does not send the flag gets no `entityDetail`. It gets the detail in two
places:

- `entityName` holds the title with the detail: `"Water (Morning, 5 of 6)"`.
- The answer text of a chat turn gets one line for each item that has a `title` field,
  because the row of such an item shows the new title: `Water (Morning, 5 of 6) → Agua`.
  The lines are one markdown list. They are one more text part at the end of `parts` when
  `parts` has items. If not, they are at the end of `aiMessage`.

The value of a `title` field never holds the detail. `fields[].entityName` and
`changes[].entityName` stay the plain title. The text is English or Brazilian Portuguese
(`5º de 6`), from the client locale, then the profile language.

## Where the detail appears

The detail is display data for an approval, so only an open preview gets it. The API
writes it when it composes the response. The stored preview, each fingerprint, the
conversation record and the model text hold the plain title. So a client can show a
different place for one preview after a different habit of the group changes, and the
preview stays current.

| Route | Detail |
|---|---|
| `POST /api/chat`, `POST /api/chat/stream` | Yes, with the answer text lines |
| `POST /api/ai/pending-operations/{id}/preview/refresh` | Yes, no text line |
| `POST /api/ai/pending-operations/{id}/revise` | Yes, no text line |
| `POST /api/ai/pending-operations/{id}/execute` | Yes, on a new pending operation in the response |
| `POST /api/ai/pending-operations/{id}/step-up/verify` | The response holds no item |
| `POST /api/ai/clarifications/{operationId}/resolve` | Yes, with the flag of the chat turn that asked |
| `POST /api/chat/habit-suggestions/preview`, `preview-all` | The title form: the request has no flag |
| `GET /api/chat/conversations/latest` | Yes, in `responseJson`, on a preview with the outcome `pending` |

A stream retry with the same `Idempotency-Key` returns the stored final event, with the
detail of the first response. The conversation page returns a preview with a different
outcome as it is stored. MCP tools, `confirm`, `step-up` and the catalog reads return no
preview item.

## Declared response types

Each `AiController` action now declares its success response type: the four catalog reads,
`revise`, `confirm`, `step-up`, `step-up/verify`, `execute` and `resolve`. The bodies do
not change. The documents publish these schema names for the first time:
`AgentCapability`, `AgentOperation`, `AgentStepUpChallenge`, `AppSurface`,
`ConfirmPendingOperationResponse`, `UserDataCatalogEntry` and `UserDataFieldDescriptor`.
