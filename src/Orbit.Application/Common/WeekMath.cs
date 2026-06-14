namespace Orbit.Application.Common;

public static class WeekMath
{
    /// <summary>
    /// Returns the first day of the calendar week containing <paramref name="day"/>, honoring the
    /// user's week-start preference (0 = Sunday, 1 = Monday). The result is the most recent
    /// <paramref name="weekStartDay"/> on or before <paramref name="day"/>.
    /// </summary>
    public static DateOnly WeekStart(DateOnly day, int weekStartDay)
    {
        var daysToStart = ((int)day.DayOfWeek - weekStartDay + 7) % 7;
        return day.AddDays(-daysToStart);
    }
}
