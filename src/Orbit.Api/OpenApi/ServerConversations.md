# Server-owned AI conversations

`astra_conversation_recording` gates synchronous encrypted conversation storage,
private chat images, server history and conversation reads. Its default remains
off. Publish the matching privacy and export copy before enabling it for an
account. With the flag off, no turn is stored or read and client history remains
the input. It keeps the provider `store=false` policy and does not use `previous_response_id`.

When enabled, the API assigns a conversation id if the client omits one and
returns it in the optional `ChatResponse.conversationId`. Subsequent requests
send that id in `clientContext.conversationId`. If stored rows exist, the API
ignores the accepted legacy `history` field. A new id can initially use client history.

The existing `ConversationTurns` table stores both ordered roles, encrypted user
text, assistant text and response parts, tool calls, compact tool results and
preview outcomes. Both rows are written before a successful final response.
Full response parts and text have no diagnostic capture limit; display and export
retain the negotiated response. Persistence failures fail the request. The account
row lock orders proposal creation, outcome snapshot updates and turn insertion
with reset and account deletion. Reset's clear timestamp and the existence check
for an already loaded conversation reject stale proposals before persistence.
An erased conversation does not fail the reply or restore content: erasure
interrupting a proposal or a final recording returns the existing localized
conversation-unavailable message without storing the stale turn. The final reply
omits stale parts, proposals and eval traces so idempotency cannot cache deleted
text. Idempotent turn ids do not create duplicate stored pairs.

Tool summaries use the same structural shortening algorithm as live tool results:
retained data, original counts, `has_more`, shortened field paths and a truncation
marker. The stored tool-results field is bounded to 12,002 characters. Preview
arguments are bounded to 8,000 characters per preview using that algorithm.
Approvals, edits and rejections reload and update the encrypted outcome snapshot
under that account lock with the pending operation id and revision. Independent
proposal transitions preserve one another, including transitions before the first
snapshot is inserted. With recording disabled, transitions do not read or update
conversation rows. Approval alone does not prove execution
succeeded. Conversation deletion also erases linked pending operations and
idempotency response copies, so they cannot restore the deleted conversation.

Model messages are ordered static prompt, account context, stored or client
history, dynamic context, then the new user message. Normal turns reuse the
recording flag from the existing concurrent moderation/context pipeline;
conversation preparation does not serialize those reads. Server history selects the
last ten rows, retaining the newest whole entries within the existing 24,000
character budget. History is quoted untrusted data. The most recent image is
included only if its user row is among the last six rows, including assistant
rows: three complete exchanges. No text pattern selects an image or a tool.
The model receives stable image bytes as `input_image`, rather than expiring
signed URLs that would change the cache prefix. History carries compact response
parts, tool results, preview arguments and outcomes alongside assistant text.

Chat images use the existing `Storage:S3` bucket and credentials under
`{accountId}/conversations/{conversationId}/{turnId}.{extension}`. No public S3
URL or ACL is created. The row stores only the key, content type and SHA-256;
reads verify the checksum. Reset and deletion drain the scoped conversation
prefix, including any orphan left by a failed database commit. The public upload route
cannot address these nested keys. Display reads use an authorized account-scoped image
endpoint with `Cache-Control: private, no-store`.

## HTTP contract

All routes require the same signed-in account and chat scope as chat requests.
A read-only credential cannot delete a conversation. An unavailable conversation
or another account's id returns `404` with `CONVERSATION_NOT_FOUND`.

- `GET /api/chat/conversations/latest?pageSize=40` returns `conversationId`,
  `turns` in ascending display order and an optional `beforeOrder` cursor.
  The page size is from 1 to 100. To load an earlier page, pass the returned
  `conversationId` and `beforeOrder` so a newer conversation cannot change the
  pagination target. No conversation returns `404`.
- `GET /api/chat/conversations/{conversationId}/images/{turnId}` returns image
  bytes to the owning account while the flag is enabled. Display turns include
  its relative API URL and file name, content type and SHA-256.
- `DELETE /api/chat/conversations/{conversationId}` removes stored rows, private
  images, offers, linked pending operations and terminal response copies; it
  returns `204`. Deletion remains available with the recording flag off.

## Retention and export

There is no time-based `ConversationTurns` expiry. `ChatTurnRetentionJob`, registered
as `chat-turn-retention` with cron `15 * * * *` through Hangfire, purges
only `ChatTurns` idempotency rows whose `ExpiresAtUtc` is more than 30 days old.
This includes terminal response copies and runs independently of the recording
flag. Unexpired and recently expired idempotency rows remain. Fresh Start and
account erasure delete the rows and objects. A conversation delete removes that
conversation's rows and objects.
Export includes all retained text, assistant response parts, tool summaries,
preview outcomes, conversation ids, display order and image file names. It does
not inline image bytes or expose storage keys.

There is no active account-wide AI-enabled setting. `User.AiMemoryEnabled` is a
compatibility field with no mutator; `GetProfileQuery` retains the response field
but returns `false`. `AiMemoryRemovalTests` verifies that the old `SetAiMemory`
route and capability are absent. `User.SetAiSummary` controls summaries alone and
`User.SetProactiveAiEnabled` controls proactive suggestions alone.
Neither is a chat retention control. Disabling the recording rollout flag stops
new storage and history reads, preserves existing rows for export or deletion,
and does not silently erase them. Privacy copy must describe these exact rules.

### Data export and account erasure

The profile data export returns the portable data of the account while the account exists. Erasure removes the account and the data that it owns. The obligation to cancel a provider subscription is billing reconciliation data. It survives erasure in `OwedSubscriptionCancellations`. The table has no Orbit user identifier, so no join returns the account. The profile export does not include it, because the erasure creates the record.

Each record keeps these fields:

- a random record ID
- the provider (Stripe or Google Play)
- the Stripe subscription ID or the Play purchase token
- the attempt count
- the creation time, the last attempt time, the next attempt time and the exhaustion time

A record keeps no name, no email, no Orbit user ID, no payment card and no provider error text. Treat a purchase token and a subscription ID as sensitive billing data. An alert uses only the record ID, the provider and the attempt count.

After the seven-day billing deferral, the erasure commits the cancellation obligation of each retained provider identifier in the same transaction that deletes the account. An error alert reports an erasure with unconfirmed renewals.

The cancellation worker scans every minute in both scheduler modes. It does not depend on the erased account or on the original queued job. It waits one minute, five minutes, then one hour between attempts. After 27 attempts, it tries once each day. These results remove the record at once:

- a successful cancellation
- a renewal that is already stopped
- a subscription that the provider confirms as missing

A failed provider does not block the obligation of another provider.

An exhausted record stays until billing reconciliation confirms that the renewal stopped. No time-based expiry removes it, because an expiry could hide an active charge. Exhaustion sends an error alert with the record ID and the provider. The alert repeats each day until the record is resolved. A billing operator must find the record, stop the renewal with the named provider, and remove the record only after the provider confirms the cancellation or confirms that the subscription does not exist.

The next attempt time schedules both the daily attempts and the exhaustion reminders. Each attempt commits before the provider calls, and the provider calls run outside database transactions. An interrupted attempt stays due and retries after the backoff. A provider cancellation that succeeds before a database failure is safe to verify again on the next scan.
