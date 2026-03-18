using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Habits.Services;

public static class HabitScheduleService
{
    /// <summary>
    /// Determines if a habit is scheduled on a given date based on its
    /// frequency, quantity, active days, and anchor (due) date.
    /// </summary>
    public static bool IsHabitDueOnDate(Habit habit, DateOnly target)
    {
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
    private const int MaxRangeDays = 366;

    public static List<DateOnly> GetScheduledDates(Habit habit, DateOnly from, DateOnly to)
    {
        // Cap the range to prevent runaway iteration on absurd date ranges
        if (to.DayNumber - from.DayNumber > MaxRangeDays)
            to = from.AddDays(MaxRangeDays);

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
