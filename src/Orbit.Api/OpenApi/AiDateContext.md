# AI dates and conversation context

The existing optional `clientContext.conversationId` links pending operations to
an account and conversation. The newest preview batch comes from those existing
operations, including their current arguments, revision and approval, edit,
rejection or expiry outcome. Approval is a proposal outcome, not independent
proof that execution succeeded. The link is retained when a proposal is revised.
The batch uses the turn's UTC creation timestamp in the existing `CreatedAtUtc`
column. Queries require both the account and conversation identifiers.

Server-owned history and image continuity are defined in
[ServerConversations.md](ServerConversations.md). The
`astra_conversation_recording` privacy gate now controls synchronous encrypted
turn storage and server history. With it off, client history remains the input.
Account reset, erasure, conversation deletion and export cover the stored turns
and private images according to that contract.

One-time creation previews always expose an editable `due_date` with type `date`.
A date or clock time supplied by the structured model plan is `requested`; the
server's date default is `suggested`. Internal metadata preserves that distinction
after preparation and reopening. Editing the date clears the default marker.
The model receives account-local today and timezone, and instructions to resolve
dates implied by words, holidays, weekdays or images. The server does not route
tools or substitute answers using message patterns.

Every provider tool-result path uses the same payload boundary. Small payloads
remain intact. Large payloads retain data under `data` with `truncated: true`, the
original serialized length, shortened field paths and original/returned counts
for shortened collections with `has_more: true`. Existing query totals remain
authoritative. The full typed result still feeds server composition and cards.
Tools with offsets or cursors can retrieve further results; otherwise the model
can narrow filters. A partial read is never evidence that omitted items do not exist.
