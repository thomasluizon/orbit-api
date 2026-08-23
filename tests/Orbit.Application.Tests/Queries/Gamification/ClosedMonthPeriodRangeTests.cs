using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Application.Tests.Queries.Gamification;

public class ClosedMonthPeriodRangeTests
{
    [Fact]
    public void Resolve_ClosedMonth_ReturnsCalendarBounds()
    {
        var result = ClosedMonthPeriodRange.Resolve(2026, 2, new DateOnly(2026, 3, 1));

        result.IsSuccess.Should().BeTrue();
        result.Value.DateFrom.Should().Be(new DateOnly(2026, 2, 1));
        result.Value.DateTo.Should().Be(new DateOnly(2026, 2, 28));
    }

    [Theory]
    [InlineData(2026, 3, 15)]
    [InlineData(2026, 4, 1)]
    public void Resolve_MonthThatHasNotClosed_ReturnsNamedFailure(int year, int month, int todayDay)
    {
        var today = new DateOnly(2026, 3, todayDay);

        var result = ClosedMonthPeriodRange.Resolve(year, month, today);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.RecapMonthNotClosed);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10000, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    public void Resolve_InvalidCalendarValues_ReturnsNamedFailure(int year, int month)
    {
        var result = ClosedMonthPeriodRange.Resolve(year, month, new DateOnly(2026, 3, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidClosedMonthParameters);
    }
}
