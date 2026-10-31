# Next repeat reminder

Tracks thomasluizon/orbit-tickets#1870.

`HabitScheduleItem`, `HabitScheduleChildItem`, `HabitDetailResponse` and
`HabitChildResponse` append optional nullable `nextReminderAtUtc`, a UTC
`date-time`. Schedule, calendar, ordinary detail and full detail reads populate
it recursively. An absent field identifies a producer without this capability;
explicit null means no repeat notification remains under the current state.

The instant is the next eligible scheduler dispatch, including the single
ignored-reminder follow-up. When an unsent occurrence is already overdue,
the field is the current eligible opportunity. Its saved `reminderAtUtc`
retains the cadence instant, so the two values can differ. Delivery still
occurs on a scheduler tick and depends on successful notification delivery.

`RepeatReminderPlanner` supplies both the scheduler and response reader.
It uses `RepeatReminderClock`, the original dose eligibility from
`RepeatReminderDoseLoader`, saved consumption and follow-up state, the account
timezone, active windows, daily stop and resume, and the local end date.
Every schedule accepts repeat reminders. Scheduled habits notify only while
due today or overdue under the schedule read model, and stop for the local day
after completion or skip. Flexible habits also respect their remaining period
target. General habits follow their own completion state. Dose correction and notification
Done read back the resulting persisted anchor without a response cache.

No input, enum or existing response field changes. Existing repeat-option
validators and domain guards still govern the supported cadence and active window.
The reader batches state for the repeat habits visible in a response, with an
ownership filter before loading their doses and reminder state.

Deploy the producer to staging through the Release API workflow before moving
the paired UI contract pin. Verify the selected producer commit and a schedule
or detail response containing `nextReminderAtUtc`, then deploy the producer
before releasing clients that depend on this field. No migration, backfill,
secret or configuration change is required.
