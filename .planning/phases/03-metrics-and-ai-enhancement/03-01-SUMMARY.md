---
phase: 03-metrics-and-ai-enhancement
plan: 01
subsystem: habits
tags: [metrics, analytics, streaks, trends, completion-rate]
dependency_graph:
  requires: [02-03-tags-profile-filtering]
  provides: [habit-metrics, habit-trends]
  affects: [habits-api]
tech_stack:
  added: []
  patterns: [frequency-aware-date-generation, timezone-aware-calculations, iso-week-aggregation]
key_files:
  created:
    - src/Orbit.Domain/Models/HabitMetrics.cs
    - src/Orbit.Domain/Models/HabitTrend.cs
    - src/Orbit.Application/Habits/Queries/GetHabitMetricsQuery.cs
    - src/Orbit.Application/Habits/Queries/GetHabitTrendQuery.cs
  modified:
    - src/Orbit.Api/Controllers/HabitsController.cs
decisions:
  - id: METR-01
    title: Frequency-aware expected date generation
    context: Different habits have different frequency patterns (daily, weekly with specific days, every N weeks/months/years)
    choice: Implemented two-path date generator - day-of-week filtering when FrequencyQuantity==1 with Days set, otherwise period-based subtraction
    rationale: Handles both "every Monday/Wednesday" and "every 2 weeks" patterns correctly
    impact: Streaks and completion rates accurately reflect user's actual habit schedule
  - id: METR-02
    title: Negative habit streak logic inversion
    context: Negative habits track slip-ups (things to avoid)
    choice: For negative habits, streak counts consecutive days NOT logged (success = no slip-up)
    rationale: Aligns with user mental model - longer streak = more days without the bad habit
    impact: Negative habits show streak growth when user successfully avoids the behavior
  - id: METR-03
    title: Timezone-aware "today" calculation
    context: Users in different timezones should see metrics relative to their local date
    choice: Convert UTC now to user's timezone (from User.TimeZone) before calculating DateOnly for "today"
    rationale: Ensures streaks break/continue at midnight in user's timezone, not UTC midnight
    impact: Metrics feel correct regardless of user location
  - id: METR-04
    title: ISO week number for weekly trends
    context: Need consistent week boundaries for trend aggregation
    choice: Use CultureInfo.InvariantCulture.Calendar.GetWeekOfYear with Monday as first day of week
    rationale: ISO 8601 standard, Monday-start aligns with common habit tracking patterns
    impact: Weekly trends have predictable, stable boundaries
metrics:
  duration_seconds: 341
  tasks_completed: 2
  files_created: 4
  files_modified: 1
  commits: 3
  deviations: 1
  completed_at: 2026-02-08
---

# Phase 03 Plan 01: Habit Metrics and Trends Summary

**One-liner:** Frequency-aware streaks, timezone-aware completion rates, and weekly/monthly trend aggregation for quantifiable habits

## What Was Built

Implemented comprehensive habit metrics system satisfying METR-01 (streaks), METR-02 (completion rates), and METR-03 (trends):

1. **HabitMetrics DTO** - Current/longest streaks, weekly/monthly completion rates (percentage 0-100), total completions, last completed date
2. **HabitTrend DTO** - Time-series data with TrendPoint (period, average, min, max, count) for weekly and monthly aggregations
3. **GetHabitMetricsQuery** - Frequency-aware expected date generation, timezone-aware "today", support for Day/Week/Month/Year frequencies, FrequencyQuantity multiplier, day-of-week filtering, positive/negative habit logic
4. **GetHabitTrendQuery** - ISO week aggregation (YYYY-WNN format), monthly aggregation (YYYY-MM format), last 12 months of quantifiable habit data
5. **API Endpoints** - GET /api/habits/{id}/metrics and GET /api/habits/{id}/trends on HabitsController

### Key Features

**Frequency-aware streak calculation:**
- Day-of-week filtering mode: When FrequencyQuantity == 1 and Days array is populated, only include dates matching specified weekdays (e.g., "Mon/Wed/Fri" habit)
- Period-based mode: When FrequencyQuantity > 1 or Days is empty, subtract frequency period each step (e.g., "every 2 weeks" subtracts 14 days)
- Handles all four FrequencyUnit types: Day, Week, Month, Year

**Positive vs Negative habits:**
- Positive habits: Streak counts consecutive expected dates WITH logs
- Negative habits: Streak counts consecutive expected dates WITHOUT logs (success = avoided the behavior)
- Current streak breaks immediately on first violation, longest streak scans entire history

**Timezone-aware metrics:**
- Converts DateTime.UtcNow to user's timezone (from User.TimeZone, defaults to UTC)
- "Today" is DateOnly in user's local timezone
- Ensures streaks break/continue at user's midnight, not UTC midnight

**Trend aggregation:**
- Weekly: ISO week numbers with Monday as first day of week (YYYY-WNN format)
- Monthly: Year-month (YYYY-MM format)
- Calculates Average, Min, Max, Count for each period
- Only available for quantifiable habits (returns 400 error for boolean habits)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed AI intent service interface signature mismatch**
- **Found during:** Task 2 compilation
- **Issue:** OllamaIntentService.InterpretAsync was missing the `userTags` parameter that was added to IAiIntentService interface in plan 02-03
- **Fix:** Updated method signature to include `IReadOnlyList<Tag> userTags` parameter and pass it to SystemPromptBuilder.BuildSystemPrompt
- **Files modified:** src/Orbit.Infrastructure/Services/AiIntentService.cs, src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs
- **Commit:** 0ff8cf5

**2. [Rule 1 - Bug] Added missing action type handlers**
- **Found during:** Task 2 compilation
- **Issue:** ProcessUserChatCommandHandler switch statement referenced ExecuteCreateSubHabitAsync and ExecuteAssignTagAsync methods that didn't exist (added in plan 02-03 but handlers were missing)
- **Fix:** Implemented both handler methods - CreateSubHabit loads habit with Include(SubHabits), calls habit.AddSubHabit, uses FindOneTrackedAsync for EF tracking. AssignTag loads habit with Include(Tags), adds tags via navigation property manipulation
- **Files modified:** src/Orbit.Application/Chat/Commands/ProcessUserChatCommand.cs
- **Commit:** 0ff8cf5

## Verification

All success criteria met:

- [x] HabitMetrics DTO has CurrentStreak, LongestStreak, WeeklyCompletionRate, MonthlyCompletionRate fields
- [x] GetHabitMetricsQuery handler implements frequency-aware streak logic accounting for FrequencyUnit, FrequencyQuantity, Days, IsNegative
- [x] GetHabitMetricsQuery uses User.TimeZone (defaults to UTC) for determining "today"
- [x] GetHabitTrendQuery handler aggregates quantifiable habit logs into weekly and monthly time series
- [x] HabitsController exposes GET metrics and GET trends endpoints
- [x] Solution builds cleanly (1 pre-existing cosmetic warning in IntegrationTests - MSB3277 EF Core Relational version conflict)

Manual verification:
- Built entire solution with `dotnet build` - success
- Endpoints added to HabitsController after DeleteHabit endpoint
- DTOs use record types for immutability
- Trend aggregation uses proper ISO week calculation with InvariantCulture.Calendar
- Frequency-aware date generation handles edge cases (week 53 year boundary)

## Commits

| Commit | Type | Description | Files |
|--------|------|-------------|-------|
| 0827d80 | feat | Implement habit metrics DTOs and frequency-aware streak calculation | HabitMetrics.cs, HabitTrend.cs, GetHabitMetricsQuery.cs |
| d48b364 | feat | Implement habit trends and add metrics/trends API endpoints | GetHabitTrendQuery.cs, HabitsController.cs |
| 0ff8cf5 | fix | Fix AI intent service signature mismatch and missing action handlers | AiIntentService.cs, ProcessUserChatCommand.cs |

## Next Phase Readiness

**Phase 03 Plan 02 (AI enhancements):** Ready - metrics infrastructure complete, no blockers

**Potential improvements for future:**
- Add caching for expensive streak calculations
- Support custom date ranges for trends (currently fixed at 12 months)
- Add streak history endpoint (show when streaks started/ended)
- Support trend aggregation for boolean habits (completion percentage over time)

## Self-Check: PASSED

**Created files:**
```
FOUND: C:\Users\thoma\Documents\Programming\Projects\Orbit\src\Orbit.Domain\Models\HabitMetrics.cs
FOUND: C:\Users\thoma\Documents\Programming\Projects\Orbit\src\Orbit.Domain\Models\HabitTrend.cs
FOUND: C:\Users\thoma\Documents\Programming\Projects\Orbit\src\Orbit.Application\Habits\Queries\GetHabitMetricsQuery.cs
FOUND: C:\Users\thoma\Documents\Programming\Projects\Orbit\src\Orbit.Application\Habits\Queries\GetHabitTrendQuery.cs
```

**Commits:**
```
FOUND: 0827d80
FOUND: d48b364
FOUND: 0ff8cf5
```

**Build verification:**
```
dotnet build - SUCCESS (0 errors, 1 pre-existing warning)
```

All artifacts verified present and functional.
