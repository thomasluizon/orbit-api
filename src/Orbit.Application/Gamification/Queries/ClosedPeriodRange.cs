using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Gamification.Queries;

public readonly record struct ClosedPeriod(DateOnly DateFrom, DateOnly DateTo);

public static class ClosedPeriodRange
{
    public static Result<ClosedPeriod> ResolveWeek(DateOnly weekStart, DateOnly userToday, int weekStartDay)
    {
        if ((int)weekStart.DayOfWeek != weekStartDay
            || weekStart.DayNumber > DateOnly.MaxValue.DayNumber - 6)
            return Result.Failure<ClosedPeriod>(ErrorMessages.InvalidClosedWeekParameters);

        var dateTo = weekStart.AddDays(6);
        if (dateTo >= userToday)
            return Result.Failure<ClosedPeriod>(ErrorMessages.RecapWeekNotClosed);

        return Result.Success(new ClosedPeriod(weekStart, dateTo));
    }

    public static Result<ClosedPeriod> ResolveYear(int year, DateOnly userToday)
    {
        if (year is < 1 or > 9999)
            return Result.Failure<ClosedPeriod>(ErrorMessages.InvalidClosedYearParameters);

        var dateTo = new DateOnly(year, 12, 31);
        if (dateTo >= userToday)
            return Result.Failure<ClosedPeriod>(ErrorMessages.RecapYearNotClosed);

        return Result.Success(new ClosedPeriod(new DateOnly(year, 1, 1), dateTo));
    }
}
