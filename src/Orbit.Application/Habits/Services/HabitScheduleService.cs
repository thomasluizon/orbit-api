using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Habits.Services;

public record HabitInstanceItem(
    DateOnly Date,
    InstanceStatus Status,
    Guid? LogId,
    string? Note);

public static class HabitScheduleService
{
    /// <summary>
    /// Determines if a habit is scheduled on a given date based on its
    /// frequency, quantity, active days, and anchor (due) date.
    /// For flexible habits, they appear on every date in range (filtering by log count is done separately).
    /// </summary>
    public static bool IsHabitDueOnDate(Habit habit, DateOnly target)
    {
        // EndDate check: habit is never due after its end date
        if (habit.EndDate.HasValue && target > habit.EndDate.Value)
            return false;

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
            FrequencyUnit.Month => IsMonthlyMatch(target, anchor, qty),
            FrequencyUnit.Year => IsYearlyMatch(target, anchor, qty),
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
    /// Count of completion logs (Value > 0) within the window containing target.
    /// Skip logs (Value == 0) are excluded.
    /// </summary>
    public static int GetCompletedInWindow(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs)
    {
        var start = GetWindowStart(habit, target);
        var end = GetWindowEnd(habit, target);
        return logs.Count(l => l.Date >= start && l.Date <= end && l.Value > 0);
    }

    /// <summary>
    /// Count of skip logs (Value == 0) within the window containing target.
    /// </summary>
    public static int GetSkippedInWindow(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs)
    {
        var start = GetWindowStart(habit, target);
        var end = GetWindowEnd(habit, target);
        return logs.Count(l => l.Date >= start && l.Date <= end && l.Value == 0);
    }

    /// <summary>
    /// How many more completions are needed in the window containing target.
    /// Skips reduce the target count for the period.
    /// </summary>
    public static int GetRemainingCompletions(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs)
    {
        var targetCount = habit.FrequencyQuantity ?? 1;
        var skipped = GetSkippedInWindow(habit, target, logs);
        var adjustedTarget = Math.Max(0, targetCount - skipped);
        var completed = GetCompletedInWindow(habit, target, logs);
        return Math.Max(0, adjustedTarget - completed);
    }


    /// <summary>
    /// Computes per-date instances for a habit within a date range, including an overdue lookback window.
    /// Each instance has its own status (Pending, Completed, Overdue) based on logs and the user's today.
    /// </summary>
    public static List<HabitInstanceItem> GetInstances(
        Habit habit,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday,
        int overdueWindowDays = AppConstants.DefaultOverdueWindowDays)
    {
        // Completed habits or flexible habits do not produce per-date instances
        if (habit.IsCompleted || habit.IsFlexible)
            return [];

        // One-time tasks: single instance on DueDate
        if (habit.FrequencyUnit is null)
        {
            if (habit.DueDate >= dateFrom && habit.DueDate <= dateTo)
            {
                var log = habit.Logs.FirstOrDefault(l => l.Date == habit.DueDate);
                var status = log is not null
                    ? InstanceStatus.Completed
                    : habit.DueDate < userToday ? InstanceStatus.Overdue : InstanceStatus.Pending;
                return [new HabitInstanceItem(habit.DueDate, status, log?.Id, log?.Note)];
            }
            return [];
        }

        // Compute the overdue lookback start (capped by habit's DueDate -- no instances before anchor)
        var lookbackStart = dateFrom.AddDays(-overdueWindowDays);
        if (lookbackStart < habit.DueDate)
            lookbackStart = habit.DueDate;

        // Get all scheduled dates from lookback through dateTo
        var scheduledDates = GetScheduledDates(habit, lookbackStart, dateTo);

        // Build a lookup of logs by date for efficient matching
        var logsByDate = habit.Logs.ToDictionary(l => l.Date, l => l);

        var instances = new List<HabitInstanceItem>(scheduledDates.Count);

        foreach (var date in scheduledDates)
        {
            logsByDate.TryGetValue(date, out var log);

            InstanceStatus status;
            if (log is not null)
            {
                status = InstanceStatus.Completed;
            }
            else if (habit.IsBadHabit)
            {
                // Bad habits never show as overdue
                status = InstanceStatus.Pending;
            }
            else if (date < userToday)
            {
                status = InstanceStatus.Overdue;
            }
            else
            {
                status = InstanceStatus.Pending;
            }

            instances.Add(new HabitInstanceItem(date, status, log?.Id, log?.Note));
        }

        return instances;
    }

    /// <summary>
    /// Monthly match: target day must equal anchor day (clamped to last day of month),
    /// and the month interval must align. This prevents anchor drift after short months
    /// (e.g., Jan 31 anchor still fires on Mar 31, not Mar 28).
    /// </summary>
    private static bool IsMonthlyMatch(DateOnly target, DateOnly anchor, int qty)
    {
        var expectedDay = Math.Min(anchor.Day, DateTime.DaysInMonth(target.Year, target.Month));
        return target.Day == expectedDay && TrueMod(MonthDiff(target, anchor), qty) == 0;
    }

    /// <summary>
    /// Yearly match: same month and day with interval alignment.
    /// For Feb 29 anchors in non-leap years, Feb 28 is used as the fallback firing date.
    /// </summary>
    private static bool IsYearlyMatch(DateOnly target, DateOnly anchor, int qty)
    {
        if (TrueMod(target.Year - anchor.Year, qty) != 0)
            return false;

        // Normal case: same month and day
        if (target.Month == anchor.Month && target.Day == anchor.Day)
            return true;

        // Leap-day fallback: anchor is Feb 29, target year is not a leap year, fire on Feb 28
        if (anchor.Month == 2 && anchor.Day == 29
            && target.Month == 2 && target.Day == 28
            && !DateTime.IsLeapYear(target.Year))
        {
            return true;
        }

        return false;
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
