---
key: schedule-math
display_name: Schedule and Overdue Math
related_capabilities: [habits.read]
related_surfaces: [today]
version: 1
derived_from:
  - src/Orbit.Application/Habits/Services/HabitScheduleService.cs IsMonthlyMatch
  - src/Orbit.Application/Habits/Services/HabitScheduleService.cs IsYearlyMatch
  - src/Orbit.Application/Habits/Services/HabitScheduleService.cs HasMissedPastOccurrence
  - src/Orbit.Application/Common/AppConstants.cs DefaultOverdueWindowDays
---

# Schedule and Overdue Math

A few scheduling rules surprise people because the calendar isn't uniform. Here is exactly how Orbit handles the tricky cases.

## Monthly habits clamp to the last valid day

A monthly habit fires on the same day-of-month as its anchor (due) date. When a month is too short for that day, it clamps to the **last valid day** of that month instead of drifting. A habit anchored on the 31st fires on March 31 — never March 28 — and on the last day of shorter months. This keeps "the 31st" meaning the end of the month rather than slipping earlier permanently.

## Yearly leap-day habits

A yearly habit anchored on **February 29** fires on **February 28** in non-leap years, then returns to February 29 when a leap year comes around again.

## Intervals align off the anchor date

The "every N" interval (every 2 weeks, every 3 months, and so on) is measured from the anchor date, not from the current date. The anchor is the fixed reference point the whole schedule lines up against.

## Overdue is DueDate-authoritative

A habit is **overdue when its due date has fallen before today**. The due date rests on the oldest unresolved occurrence; logging or skipping advances it past today. This single signal — due date earlier than today — is what marks a recurring habit overdue everywhere in the app.

Bad habits are never overdue (there's no "must do" expectation to miss), and flexible habits use their window instead of an overdue date.

The default overdue lookback window is **7** days (`DefaultOverdueWindowDays`): the day view surfaces unresolved occurrences from up to a week back so a missed day doesn't silently disappear.
