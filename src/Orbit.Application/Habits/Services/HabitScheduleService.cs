using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Habits.Services;

public static class HabitScheduleService
{
    /// <summary>
    /// Determines if a habit is scheduled on a given date based on its
    /// frequency, quantity, active days, and anchor (due) date.
    /// For flexible habits, they appear on every date in range (filtering by log count is done separately).
    /// </summary>
    public static bool IsHabitDueOnDate(Habit habit, DateOnly target)
    {
        if (habit.IsFlexible)
        {
            // Flexible habit: due on any date at or after its start
            if (habit.FrequencyUnit is null) return false;
            return target >= habit.DueDate;
        }

        var anchor = habit.DueDate;
        var unit = habit.FrequencyUnit;
        var qty = habit.FrequencyQuantity ?? 1;

        // One-time task: only due on its specific date
        if (unit is null)
        {
            return target == habit.DueDate;
        }

        // Habit is not due before its anchor (due) date
        if (target < anchor) return false;

        // Day filter: if habit has specific days, check target's weekday
        if (habit.Days.Count > 0)
        {
            if (!habit.Days.Contains(target.DayOfWeek))
                return false;
        }

        return unit switch
        {
            FrequencyUnit.Day => qty == 1 || TrueMod(target.DayNumber - anchor.DayNumber, qty) == 0,
            FrequencyUnit.Week => target.DayOfWeek == anchor.DayOfWeek && TrueMod(WeekDiff(target, anchor), qty) == 0,
            FrequencyUnit.Month => target.Day == anchor.Day && TrueMod(MonthDiff(target, anchor), qty) == 0,
            FrequencyUnit.Year => target.Month == anchor.Month && target.Day == anchor.Day && TrueMod(target.Year - anchor.Year, qty) == 0,
            _ => false
        };
    }

    /// <summary>
    /// Returns all dates within [from, to] where the habit is scheduled.
    /// </summary>
    public static List<DateOnly> GetScheduledDates(Habit habit, DateOnly from, DateOnly to)
    {
        // Cap the range to prevent runaway iteration on absurd date ranges
        if (to.DayNumber - from.DayNumber > AppConstants.MaxRangeDays)
            to = from.AddDays(AppConstants.MaxRangeDays);

        var dates = new List<DateOnly>();
        var current = from;
        while (current <= to)
        {
            if (IsHabitDueOnDate(habit, current))
                dates.Add(current);
            current = current.AddDays(1);
        }
        return dates;
    }

    // --- Flexible habit methods ---

    /// <summary>
    /// Checks if the flexible habit still needs completions within the window containing target.
    /// </summary>
    public static bool IsFlexibleHabitDueOnDate(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs)
    {
        if (!habit.IsFlexible || habit.FrequencyUnit is null) return false;
        if (target < habit.DueDate) return false;
        return GetRemainingCompletions(habit, target, logs) > 0;
    }

    /// <summary>
    /// Returns the start of the window containing the target date.
    /// Day = same day, Week = ISO Monday, Month = 1st, Year = Jan 1.
    /// </summary>
    public static DateOnly GetWindowStart(Habit habit, DateOnly target)
    {
        return habit.FrequencyUnit switch
        {
            FrequencyUnit.Day => target,
            FrequencyUnit.Week => target.AddDays(-(((int)target.DayOfWeek + 6) % 7)), // ISO Monday
            FrequencyUnit.Month => new DateOnly(target.Year, target.Month, 1),
            FrequencyUnit.Year => new DateOnly(target.Year, 1, 1),
            _ => target
        };
    }

    /// <summary>
    /// Returns the end of the window containing the target date.
    /// Day = same day, Week = ISO Sunday, Month = last day, Year = Dec 31.
    /// </summary>
    public static DateOnly GetWindowEnd(Habit habit, DateOnly target)
    {
        return habit.FrequencyUnit switch
        {
            FrequencyUnit.Day => target,
            FrequencyUnit.Week => GetWindowStart(habit, target).AddDays(6),
            FrequencyUnit.Month => new DateOnly(target.Year, target.Month, DateTime.DaysInMonth(target.Year, target.Month)),
            FrequencyUnit.Year => new DateOnly(target.Year, 12, 31),
            _ => target
        };
    }

    /// <summary>
    /// Count of logs within the window containing target.
    /// </summary>
    public static int GetCompletedInWindow(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs)
    {
        var start = GetWindowStart(habit, target);
        var end = GetWindowEnd(habit, target);
        return logs.Count(l => l.Date >= start && l.Date <= end);
    }

    /// <summary>
    /// How many more completions are needed in the window containing target.
    /// </summary>
    public static int GetRemainingCompletions(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs)
    {
        var targetCount = habit.FrequencyQuantity ?? 1;
        var completed = GetCompletedInWindow(habit, target, logs);
        return Math.Max(0, targetCount - completed);
    }

    private static int TrueMod(int n, int m)
    {
        return ((n % m) + m) % m;
    }

    private static int WeekDiff(DateOnly a, DateOnly b)
    {
        // ISO week difference
        var dayDiff = a.DayNumber - b.DayNumber;
        return dayDiff / 7;
    }

    private static int MonthDiff(DateOnly a, DateOnly b)
    {
        return (a.Year - b.Year) * 12 + (a.Month - b.Month);
    }
}
