namespace Orbit.Domain.Common;

public readonly record struct AiAllowancePeriod
{
    public DateOnly Start { get; }
    public DateOnly End { get; }
    public string Name { get; }

    private AiAllowancePeriod(DateOnly start, int days, string name)
    {
        Start = start;
        End = start.AddDays(days);
        Name = name;
    }

    public static AiAllowancePeriod For(DateOnly userToday, bool pro, int weekStartDay)
    {
        if (weekStartDay is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(weekStartDay));

        return pro
            ? new(userToday.AddDays(-((int)userToday.DayOfWeek - weekStartDay + 7) % 7), 7, "week")
            : new(userToday, 1, "day");
    }

    public static AiAllowancePeriod ActiveFor(DateOnly userToday, bool pro, int weekStartDay, DateOnly? storedWeekStart)
    {
        var preferredPeriod = For(userToday, pro, weekStartDay);
        return pro && storedWeekStart.HasValue && userToday < storedWeekStart.Value.AddDays(7)
            ? new(storedWeekStart.Value, 7, "week")
            : preferredPeriod;
    }
}
