using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Gamification.Queries;

public readonly record struct ClosedMonthPeriod(DateOnly DateFrom, DateOnly DateTo);

/// <summary>Resolves a completed calendar month from the user's local calendar.</summary>
public static class ClosedMonthPeriodRange
{
    public static Result<ClosedMonthPeriod> Resolve(int year, int month, DateOnly userToday)
    {
        if (year is < 1 or > 9999 || month is < 1 or > 12)
            return Result.Failure<ClosedMonthPeriod>(ErrorMessages.InvalidClosedMonthParameters);

        var dateFrom = new DateOnly(year, month, 1);
        var dateTo = new DateOnly(year, month, DateTime.DaysInMonth(year, month));

        if (dateTo >= userToday)
            return Result.Failure<ClosedMonthPeriod>(ErrorMessages.RecapMonthNotClosed);

        return Result.Success(new ClosedMonthPeriod(dateFrom, dateTo));
    }
}
