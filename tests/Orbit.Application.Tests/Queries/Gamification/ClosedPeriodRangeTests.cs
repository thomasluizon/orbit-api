using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Application.Tests.Queries.Gamification;

public class ClosedPeriodRangeTests
{
    [Theory]
    [InlineData(0, 2026, 8, 16, 2026, 8, 22)]
    [InlineData(1, 2026, 8, 17, 2026, 8, 23)]
    [InlineData(0, 2025, 12, 28, 2026, 1, 3)]
    public void ResolveWeek_ClosedWeek_ReturnsCalendarBounds(
        int weekStartDay, int year, int month, int day,
        int endYear, int endMonth, int endDay)
    {
        var result = ClosedPeriodRange.ResolveWeek(
            new DateOnly(year, month, day), new DateOnly(2026, 8, 24), weekStartDay);

        result.IsSuccess.Should().BeTrue();
        result.Value.DateFrom.Should().Be(new DateOnly(year, month, day));
        result.Value.DateTo.Should().Be(new DateOnly(endYear, endMonth, endDay));
    }

    [Fact]
    public void ResolveWeek_CurrentWeek_ReturnsNamedFailure()
    {
        var result = ClosedPeriodRange.ResolveWeek(
            new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 23), 1);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.RecapWeekNotClosed);
    }

    [Fact]
    public void ResolveWeek_MisalignedStart_ReturnsNamedFailure()
    {
        var result = ClosedPeriodRange.ResolveWeek(
            new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 24), 1);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidClosedWeekParameters);
    }

    [Fact]
    public void ResolveYear_ClosedLeapYear_ReturnsCalendarBounds()
    {
        var result = ClosedPeriodRange.ResolveYear(2024, new DateOnly(2025, 1, 1));

        result.IsSuccess.Should().BeTrue();
        result.Value.DateFrom.Should().Be(new DateOnly(2024, 1, 1));
        result.Value.DateTo.Should().Be(new DateOnly(2024, 12, 31));
    }

    [Theory]
    [InlineData(2026, 2026, 12, 31)]
    [InlineData(2026, 2026, 8, 24)]
    public void ResolveYear_OpenYear_ReturnsNamedFailure(int year, int todayYear, int todayMonth, int todayDay)
    {
        var result = ClosedPeriodRange.ResolveYear(year, new DateOnly(todayYear, todayMonth, todayDay));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.RecapYearNotClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public void ResolveYear_InvalidYear_ReturnsNamedFailure(int year)
    {
        var result = ClosedPeriodRange.ResolveYear(year, new DateOnly(2026, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidClosedYearParameters);
    }
}
