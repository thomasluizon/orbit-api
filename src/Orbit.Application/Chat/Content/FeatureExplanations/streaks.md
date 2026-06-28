---
key: streaks
display_name: Streaks
related_capabilities: [gamification.read]
related_surfaces: [gamification, today]
version: 1
derived_from:
  - src/Orbit.Application/Habits/Services/HabitScheduleService.cs ComputeStreakAsOf
  - src/Orbit.Infrastructure/Services/UserStreakService.cs LoadStreakDataAsync
  - src/Orbit.Infrastructure/Services/UserStreakService.cs CalendarFallback
  - src/Orbit.Application/Common/AppConstants.cs MaxStreakLookbackDays
---

# Streaks

Your streak counts how many consecutive **scheduled days** you stayed active. A scheduled day is a day where one of your recurring habits was due. The day counts toward the streak if either:

- you completed at least one eligible habit that day (a real completion, not a skip), or
- a streak freeze covered that day.

The streak is measured by walking your scheduled days in order and counting the unbroken run that reaches today. A scheduled day with a completion or a freeze keeps the run going; the first scheduled day you missed (no completion and no freeze) breaks it. Days where nothing was scheduled are simply skipped over — they neither extend nor break the streak. If today is scheduled but has no completion yet, it is treated as not-yet-missed, so an unfinished today never breaks the run.

## What counts as a completion

A completion is any log with a value greater than zero on a habit that is not deleted and not a bad habit. Skips (a zero value) do not count. Bad habits never **add** scheduled days to your streak, but completing a regular habit on the same day still counts normally — bad habits just don't create the "must do something today" expectation.

## Which habits create scheduled days

Expected (scheduled) days come only from your recurring habits that are not bad habits, not general habits, and not flexible habits. One-time tasks that you've already finished stop contributing expected days going forward. So a missed flexible-habit window or a skipped general habit will not break your streak.

## Brand-new users

If you have no recurring habits at all yet, the streak falls back to simple **calendar-day adjacency**: completing a habit on back-to-back calendar days builds the streak, so you aren't penalized before you've set up any schedule.

## Lookback limit

Streak calculation looks back at most **365** days (`MaxStreakLookbackDays`). Activity older than a year does not extend the current streak.

The longest streak is tracked separately by scanning your full scheduled-day history for the longest unbroken run; it never decreases when the current streak resets.
