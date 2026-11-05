namespace Orbit.Domain.Common;

/// <summary>
/// Calendar math for a monthly habit that repeats on chosen days of the month (1 to 31).
/// A month that lacks a chosen day puts that occurrence on the month's last day, once.
/// </summary>
public static class MonthDaySchedule
{
    public const int MinMonthDay = 1;
    public const int MaxMonthDay = 31;

    public static IReadOnlyList<int> Normalize(IEnumerable<int>? monthDays) =>
        monthDays?.Distinct().Order().ToList() ?? [];

    public static bool IsValid(IReadOnlyCollection<int> monthDays) =>
        monthDays.All(day => day is >= MinMonthDay and <= MaxMonthDay)
        && monthDays.Distinct().Count() == monthDays.Count;

    public static bool Matches(IReadOnlyCollection<int> monthDays, DateOnly date)
    {
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        return monthDays.Any(day => Math.Min(day, daysInMonth) == date.Day);
    }

    /// <summary>The earliest occurrence in the month of <paramref name="date"/> on or after it, if any.</summary>
    public static DateOnly? FirstInMonthOnOrAfter(IReadOnlyCollection<int> monthDays, DateOnly date)
    {
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        var day = monthDays
            .Select(monthDay => Math.Min(monthDay, daysInMonth))
            .Where(effective => effective >= date.Day)
            .DefaultIfEmpty(0)
            .Min();
        return day == 0 ? null : new DateOnly(date.Year, date.Month, day);
    }

    public static DateOnly FirstInMonth(IReadOnlyCollection<int> monthDays, int year, int month) =>
        FirstInMonthOnOrAfter(monthDays, new DateOnly(year, month, 1))
        ?? throw new ArgumentException("At least one month day is required.", nameof(monthDays));
}
