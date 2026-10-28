# Repeat reminder Android push

Tracks thomasluizon/orbit-tickets#1871. The paired Android client is
thomasluizon/orbit-tickets#1860.

Repeat reminders delivered through FCM use a data message with high Android
priority and no OS-rendered notification block. The Android Expo service reads
`title`, `message` (visible body), `categoryId` and `channelId` from that data.
The fixed category identifier is `orbit-repeat-reminder`, defined by
`PushNotificationService.RepeatReminderCategoryId`. Clients register their Done
action under that identifier. The channel is the client's existing `default`.

The existing `habitId`, `tag`, `reminderAtUtc`, `doneExpiresAtUtc`, `doneToken`,
`doneLabel` and `url` data keys retain their values. Expo uses `tag` as the
notification identifier to replace an earlier reminder for the same habit.
No JSON `body` wrapper is needed: Expo forwards the flat FCM data to the
notification response, preserving the one-use token and existing route.

An Android build without the category registered still presents the title and
message with its default tap action, carrying the same route and habit ID.
It has no Done button. A missing channel falls back to Expo's notification
channel. This fallback requires no capability flag or minimum-version change.

Ordinary reminders, Astra, slip alerts and non-FCM transports retain their
payloads. No endpoint, request or response schema changes.

Deploy the API to staging through the Release API workflow before validating
the paired Android build. In an Android build with the category registered,
configure a habit to repeat every 15 minutes, close the app, and verify that
the notification has Done, replaces the previous reminder, opens the existing
habit route on a body tap and accepts its signed Done action once. Repeat
without category registration to verify plain presentation and the body tap.
The API worker performs source inspection and unit tests; device verification
belongs to the paired client rollout.
