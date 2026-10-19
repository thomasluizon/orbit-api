# Astra conversation recording

Collection is disabled by default by the `astra_conversation_recording` row in
`AppFeatureFlags`. The feature uses the same database feature flag and account
allowlist resolver as Astra's other flags. Publishing the disclosure owned by
thomasluizon/orbit-tickets#104 is required before enabling collection.

Each interaction produces two `ConversationTurns` rows sharing `TurnId`: the user's
text and the assistant's final text. Assistant rows include ordered attempted tool
names, call identifiers, arguments, success indicators and original error messages.
Only attempted tools are included. Full tool result payloads, uploaded images, audio,
prior client-supplied history and authentication credentials are not added as separate
fields. A user can still type sensitive information into a message or tool argument.
The configured `AI:Model` is recorded for model-backed replies; cached and static
replies have no model. Content and the complete tool-call JSON use the existing field
encryption converters. Recording requires configured encryption and never falls back
to plaintext.

## Retention and capacity

The retention window is 30 days: this allows investigation after a delayed failure
report while limiting the amount of sensitive diagnostic text retained.
`conversation-retention` runs hourly at minute 15 through Hangfire in either scheduler
mode. Rows become eligible for deletion at 30 days; under normal operation deletion
occurs within the following hour. Export filters to the last 30 days even between
cleanup runs. No archive is maintained. Reset and deletion remove the user's records;
a reset timestamp and the existing user-row lock reject older queued writes, and the
user foreign key cascades on deletion.

Capacity planning is a scenario, not measured traffic: 100 daily active Astra users
at 10 interactions per day produce `100 * 10 * 2 * 30 = 60,000` rows at steady state.
At an assumed average of 4 KiB of ciphertext and metadata per row, that is about
234 MiB before indexes and PostgreSQL storage overhead. Scale linearly with daily
interactions and measure the actual average row size after rollout. Compare row count
and `pg_total_relation_size('"ConversationTurns"')` weekly for the first month. If
storage outgrows this window's prediction, shorten the window.

The in-memory queue holds 64 interactions, drains batches of at most 16 in fresh DI
scopes, and accepts at most 65,536 combined text characters per interaction. Queue
saturation, oversized records, missing encryption and storage failures drop diagnostic
records with warnings. The request never awaits database recording. Shutdown drains
for at most five seconds, subject to the host's remaining shutdown deadline. A crash
can lose the queue. Existing awaited mutation persistence is unaffected.

## Disclosure for the privacy-policy ticket

The following text can be used verbatim by the paired policy change:

> When you use Astra, we may store the text of your messages and Astra's replies,
> together with timestamps, the AI model, and attempted tool names, arguments,
> success indicators and error messages. We encrypt this conversation content at
> rest and use it to investigate failures and evaluate Astra's reliability. The
> retention window is 30 days, with expired records removed automatically by an
> hourly cleanup job. Your data export includes only your conversations still within
> this window; we do not keep an archive for exports. Resetting or deleting your
> account removes these conversation records.

ORB-98 owns publication in both English and Brazilian Portuguese on web and mobile.
This repository does not change or publish the policy.

## Activation and recovery

After the disclosure is live on web and mobile in both languages, an operator can
turn on `AppFeatureFlags.Enabled` for `Key = 'astra_conversation_recording'` in the
production database console, updating `UpdatedAtUtc` and recording that timestamp in
the owning ticket. Read the row back and confirm newly recorded raw `Content` and
`ToolCallsJson` columns carry ciphertext. Flag changes take effect within the
existing feature flag cache TTL of 30 seconds. The consumer also checks the flag. Static block answers can enter the bounded
in-memory queue before flag resolution; only the consumer writes them, and it drops
them while collection is disabled. This avoids adding a database read to those replies.

To stop collection, set that row's `Enabled` to false and remove any
`AppFeatureFlagAccounts` entries whose `FlagKey = 'astra_conversation_recording'`;
account allowlists otherwise continue to enable a disabled global flag. Read both
back. Retention and export continue to work with collection disabled.

Reverting the code and applying the migration's `Down` drops the conversation table
and its contents. This deliberately loses diagnostic data. It does not undo a habit
mutation or alter the user's functional account data.
