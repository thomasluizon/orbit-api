---
key: frequencies
display_name: Habit Frequencies
related_capabilities: [habits.read, habits.write]
related_surfaces: [today]
version: 1
derived_from:
  - src/Orbit.Application/Habits/Services/HabitScheduleService.cs IsHabitDueOnDate
  - src/Orbit.Application/Habits/Services/HabitScheduleService.cs GetWindowStart
  - src/Orbit.Application/Habits/Services/HabitScheduleService.cs GetWindowEnd
  - src/Orbit.Application/Habits/Services/HabitScheduleService.cs GetRemainingCompletions
---

# Habit Frequencies

A habit's frequency decides which days it is due. Every recurring habit has a unit (Day, Week, Month, or Year), an interval quantity (how many of those units between occurrences), and an anchor date — the habit's due date — that the schedule aligns to.

## The frequency units

- **Daily** — due every day, or every N days when the interval is greater than 1 (for example, every 2 days). The interval is counted from the anchor date.
- **Weekly** — due on the same weekday as the anchor, every N weeks (for example, every 2 weeks on Monday).
- **Monthly** — due on the same day-of-month as the anchor, every N months.
- **Yearly** — due on the same month and day as the anchor, every N years.

The interval quantity is the "every N" part. With a quantity of 1 the habit is due every period; with a quantity of 2 it is due every other period, and so on. A habit is never due before its anchor date, and never after its end date if one is set.

## Specific weekdays

A habit can also restrict itself to specific weekdays. When weekdays are chosen, the habit is only due on a matching date if that date's weekday is in the list. This layers on top of the unit and interval.

## One-time tasks

A one-time task has no recurring unit. It is due on exactly one date — its due date — and nowhere else. Once completed, it stops appearing.

## Flexible habits

A flexible habit doesn't pin you to specific days. Instead it asks for **N completions per window**, where the window is one Day, one Week, one Month, or one Year. Weekly windows run Monday through Sunday (ISO week). Within a window you can log on any days you like until you hit the target.

Skips make flexible targets more forgiving: each skip in the window reduces the number of completions still required for that window. So if a weekly flexible habit wants 3 completions and you skip once, only 2 completions are needed that week.
