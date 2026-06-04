---
key: notifications
display_name: Reminders and Notifications
related_capabilities: [notifications.read, notifications.write]
related_surfaces: [notifications]
version: 1
derived_from:
  - src/Orbit.Infrastructure/Services/ReminderSchedulerService.cs ProcessRelativeReminders
  - src/Orbit.Infrastructure/Services/ReminderSchedulerService.cs ProcessScheduledReminders
  - src/Orbit.Infrastructure/Services/ReminderSchedulerService.cs ShouldSendScheduledReminder
---

# Reminders and Notifications

Reminders are sent by a background job that checks roughly **every minute**. There are two kinds, depending on whether a habit has a due time.

## Relative reminders (habits with a due time)

If a habit has a specific due **time**, you can set "X minutes before" reminders. The job fires each one when the current local time reaches that many minutes before the due time. A habit can have several relative reminders (for example, 30 minutes before and 10 minutes before).

## Scheduled reminders (habits without a due time)

If a habit has no due time, it uses **scheduled** reminders that fire at a time you pick, either:

- **same-day** — on the day the habit is due, or
- **day-before** — the day before it's due.

## When reminders fire

A reminder is only sent for a habit that is:

- not completed and not a general habit,
- has reminders enabled,
- is actually **due** that day, and
- has **not yet been logged** that day.

Each distinct reminder is sent **once** — once a given reminder has fired for a habit on a given day, it won't fire again, so you won't be nudged twice for the same thing.
