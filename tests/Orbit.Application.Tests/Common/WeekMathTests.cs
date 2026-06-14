using FluentAssertions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class WeekMathTests
{
    [Theory]
    [InlineData(2025, 4, 7, 2025, 4, 7)]
    [InlineData(2025, 4, 8, 2025, 4, 7)]
    [InlineData(2025, 4, 9, 2025, 4, 7)]
    [InlineData(2025, 4, 10, 2025, 4, 7)]
    [InlineData(2025, 4, 11, 2025, 4, 7)]
    [InlineData(2025, 4, 12, 2025, 4, 7)]
    [InlineData(2025, 4, 13, 2025, 4, 7)]
    public void WeekStart_MondayAnchored_ReturnsMonday(
        int year, int month, int day, int expectedYear, int expectedMonth, int expectedDay)
    {
        var result = WeekMath.WeekStart(new DateOnly(year, month, day), weekStartDay: 1);

        result.Should().Be(new DateOnly(expectedYear, expectedMonth, expectedDay));
    }

    [Theory]
    [InlineData(2025, 4, 6, 2025, 4, 6)]
    [InlineData(2025, 4, 7, 2025, 4, 6)]
    [InlineData(2025, 4, 8, 2025, 4, 6)]
    [InlineData(2025, 4, 9, 2025, 4, 6)]
    [InlineData(2025, 4, 10, 2025, 4, 6)]
    [InlineData(2025, 4, 11, 2025, 4, 6)]
    [InlineData(2025, 4, 12, 2025, 4, 6)]
    public void WeekStart_SundayAnchored_ReturnsSunday(
        int year, int month, int day, int expectedYear, int expectedMonth, int expectedDay)
    {
        var result = WeekMath.WeekStart(new DateOnly(year, month, day), weekStartDay: 0);

        result.Should().Be(new DateOnly(expectedYear, expectedMonth, expectedDay));
    }

    [Fact]
    public void WeekStart_OnAnchorDay_ReturnsSameDay()
    {
        var sunday = new DateOnly(2025, 4, 13);
        sunday.DayOfWeek.Should().Be(DayOfWeek.Sunday);

        WeekMath.WeekStart(sunday, weekStartDay: 0).Should().Be(sunday);
    }

    [Fact]
    public void WeekStart_CrossesMonthBoundary()
    {
        var result = WeekMath.WeekStart(new DateOnly(2025, 5, 1), weekStartDay: 1);

        result.Should().Be(new DateOnly(2025, 4, 28));
    }

    [Fact]
    public void WeekStart_MondayVersusSunday_DiffersByOneDayMidWeek()
    {
        var wednesday = new DateOnly(2025, 4, 9);

        var mondayStart = WeekMath.WeekStart(wednesday, weekStartDay: 1);
        var sundayStart = WeekMath.WeekStart(wednesday, weekStartDay: 0);

        mondayStart.Should().Be(new DateOnly(2025, 4, 7));
        sundayStart.Should().Be(new DateOnly(2025, 4, 6));
    }
}
