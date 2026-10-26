using FluentAssertions;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Tests.Queries.Habits;

public class RetrospectivePeriodRangeTests
{
    private static readonly DateOnly Today = new(2026, 6, 20);

    [Theory]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("quarter")]
    [InlineData("semester")]
    [InlineData("year")]
    public void Resolve_KnownPeriod_WindowEndsToday(string period)
    {
        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve(period, Today, weekStartDay: 1);

        dateTo.Should().Be(Today);
        dateFrom.Should().BeOnOrBefore(Today);
    }

    [Theory]
    [InlineData("month", 30)]
    [InlineData("quarter", 90)]
    [InlineData("semester", 180)]
    [InlineData("year", 365)]
    public void Resolve_RollingPeriod_SpansExpectedDateCount(string period, int expectedDateCount)
    {
        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve(period, Today, weekStartDay: 1);

        var dateCount = dateTo.DayNumber - dateFrom.DayNumber + 1;

        dateCount.Should().Be(expectedDateCount);
    }

    [Fact]
    public void Resolve_UnknownPeriod_Throws()
    {
        var act = () => RetrospectivePeriodRange.Resolve("decade", Today, weekStartDay: 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("quarter")]
    [InlineData("semester")]
    [InlineData("year")]
    [InlineData("WEEK")]
    [InlineData("Year")]
    public void IsKnownPeriod_KnownPeriod_ReturnsTrue(string period) =>
        RetrospectivePeriodRange.IsKnownPeriod(period).Should().BeTrue();

    [Theory]
    [InlineData("day")]
    [InlineData("decade")]
    [InlineData("")]
    [InlineData(null)]
    public void IsKnownPeriod_UnknownPeriod_ReturnsFalse(string? period) =>
        RetrospectivePeriodRange.IsKnownPeriod(period).Should().BeFalse();

    [Theory]
    [InlineData("2026-10-26", 1, "2026-10-26", "2026-10-19", "2026-10-25")]
    [InlineData("2026-10-26", 0, "2026-10-25", "2026-10-18", "2026-10-24")]
    [InlineData("2026-11-01", 1, "2026-10-26", "2026-10-19", "2026-10-25")]
    [InlineData("2026-11-01", 0, "2026-11-01", "2026-10-25", "2026-10-31")]
    [InlineData("2026-10-28", 1, "2026-10-26", "2026-10-19", "2026-10-25")]
    [InlineData("2026-10-28", 0, "2026-10-25", "2026-10-18", "2026-10-24")]
    public void Resolve_RelativeWeeks_UseCalendarBoundaries(string day, int start, string currentFrom,
        string previousFrom, string previousTo)
    {
        var today = DateOnly.Parse(day);
        RetrospectivePeriodRange.Resolve("this_week", today, start)
            .Should().Be((DateOnly.Parse(currentFrom), today));
        RetrospectivePeriodRange.Resolve("last_week", today, start)
            .Should().Be((DateOnly.Parse(previousFrom), DateOnly.Parse(previousTo)));
    }

    [Theory]
    [InlineData("today", "2026-10-26", "2026-10-26", "2026-10-26")]
    [InlineData("yesterday", "2026-01-01", "2025-12-31", "2025-12-31")]
    [InlineData("this_month", "2026-10-26", "2026-10-01", "2026-10-26")]
    [InlineData("last_month", "2026-01-01", "2025-12-01", "2025-12-31")]
    [InlineData("last_month", "2024-03-01", "2024-02-01", "2024-02-29")]
    [InlineData("last_7_days", "2026-10-26", "2026-10-20", "2026-10-26")]
    public void Resolve_RelativeDaysAndMonths_HaveExplicitMeaning(string period, string day, string from, string to)
    {
        RetrospectivePeriodRange.IsRelativePeriod(period).Should().BeTrue();
        RetrospectivePeriodRange.Resolve(period, DateOnly.Parse(day), 1)
            .Should().Be((DateOnly.Parse(from), DateOnly.Parse(to)));
    }

    [Theory]
    [InlineData("last_n_days", 21)]
    [InlineData("last_n_weeks", 3)]
    public void Resolve_CountedRollingPeriods_IncludeToday(string period, int count) =>
        RetrospectivePeriodRange.Resolve(period, Today, 1, count).Should().Be((Today.AddDays(-20), Today));

    [Theory]
    [InlineData("last_n_days", null)]
    [InlineData("last_n_weeks", 0)]
    [InlineData("last_n_days", 3660)]
    [InlineData("last_n_weeks", 523)]
    [InlineData("this_week", 3)]
    public void Resolve_InvalidPeriodCount_Throws(string period, int? count)
    {
        var resolve = () => RetrospectivePeriodRange.Resolve(period, Today, 1, count);
        resolve.Should().Throw<ArgumentException>();
    }
}
