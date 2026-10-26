using Orbit.Domain.Common;

namespace Orbit.Domain.Models;

public sealed class HabitCalendarWindow
{
    public const int MaxRangeDays = 62;
    public DateOnly From { get; }
    public DateOnly To { get; }

    private HabitCalendarWindow(DateOnly from, DateOnly to) => (From, To) = (from, to);

    public static Result<HabitCalendarWindow> Create(DateOnly from, DateOnly to)
    {
        if (from.DayNumber < 31 || to < from || to.DayNumber - from.DayNumber >= MaxRangeDays)
            return Result.Failure<HabitCalendarWindow>(DomainErrors.HabitCalendarRangeInvalid);
        return Result.Success(new HabitCalendarWindow(from, to));
    }

    public static Result<HabitCalendarWindow> Resolve(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var anchor = from ?? to ?? today;
        var start = from ?? new DateOnly(anchor.Year, anchor.Month, 1);
        var end = to ?? new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));
        return Create(start, end);
    }
}
