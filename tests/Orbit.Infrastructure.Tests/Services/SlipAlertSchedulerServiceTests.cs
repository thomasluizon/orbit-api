using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure helper methods of SlipAlertSchedulerService:
/// CalculateAlertTime, IsWithinSendWindow, and week-start calculation logic.
/// The main loop and DB-dependent logic are integration concerns.
/// </summary>
public class SlipAlertSchedulerServiceTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    [Theory]
    [InlineData(10, 8)]    [InlineData(12, 10)]    [InlineData(20, 18)]    [InlineData(23, 21)]    [InlineData(9, 8)]    [InlineData(8, 8)]    [InlineData(5, 8)]    public void CalculateAlertTime_WithPeakHour_ReturnsClamped(int peakHour, int expectedHour)
    {
        var result = InvokeCalculateAlertTime(peakHour);

        result.Hour.Should().Be(expectedHour);
        result.Minute.Should().Be(0);
    }

    [Fact]
    public void CalculateAlertTime_WithoutPeakHour_ReturnsMorningDefault()
    {
        var result = InvokeCalculateAlertTime(null);

        result.Hour.Should().Be(8);
        result.Minute.Should().Be(0);
    }

    [Fact]
    public void CalculateAlertTime_HighPeakHour_ClampsTo22()
    {
        var result = InvokeCalculateAlertTime(24);

        result.Hour.Should().Be(22);
    }

    [Theory]
    [InlineData(0, 8)]    [InlineData(1, 8)]    [InlineData(2, 8)]    [InlineData(3, 8)]    public void CalculateAlertTime_VeryEarlyPeakHour_ClampsToMorning(int peakHour, int expectedHour)
    {
        var result = InvokeCalculateAlertTime(peakHour);
        result.Hour.Should().Be(expectedHour);
    }

    [Fact]
    public void CalculateAlertTime_PeakHourAt10_AlertAt8()
    {
        var result = InvokeCalculateAlertTime(10);
        result.Hour.Should().Be(8);
    }

    [Fact]
    public void CalculateAlertTime_PeakHourAt24_AlertAt22()
    {
        var result = InvokeCalculateAlertTime(24);
        result.Hour.Should().Be(22);
    }

    [Fact]
    public void CalculateAlertTime_AlwaysReturnsZeroMinutes()
    {
        for (var h = 0; h <= 24; h++)
        {
            var result = InvokeCalculateAlertTime(h);
            result.Minute.Should().Be(0, $"peak hour {h} should have 0 minutes");
        }
    }

    [Fact]
    public void IsWithinSendWindow_ExactlyAtAlertTime_ReturnsTrue()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 0), new TimeOnly(10, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_WithinFiveMinutes_ReturnsTrue()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 3), new TimeOnly(10, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_ExactlyFiveMinutesAfter_ReturnsFalse()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 5), new TimeOnly(10, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_BeforeAlertTime_ReturnsFalse()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(9, 58), new TimeOnly(10, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_LongAfterAlertTime_ReturnsFalse()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(14, 0), new TimeOnly(10, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_OneMinuteAfter_ReturnsTrue()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 1), new TimeOnly(10, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_FourMinutes59Seconds_ReturnsTrue()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 4, 59), new TimeOnly(10, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_OneSecondBefore_ReturnsFalse()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(9, 59, 59), new TimeOnly(10, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_MidnightAlertTime_WorksCorrectly()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(0, 2), new TimeOnly(0, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_EndOfDay_WorksCorrectly()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(23, 58), new TimeOnly(23, 55));
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(2025, 4, 7, 2025, 4, 7)]    [InlineData(2025, 4, 8, 2025, 4, 7)]    [InlineData(2025, 4, 9, 2025, 4, 7)]    [InlineData(2025, 4, 10, 2025, 4, 7)]    [InlineData(2025, 4, 11, 2025, 4, 7)]    [InlineData(2025, 4, 12, 2025, 4, 7)]    [InlineData(2025, 4, 13, 2025, 4, 7)]    public void WeekStartCalculation_ReturnsMonday(
        int year, int month, int day,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var userToday = new DateOnly(year, month, day);
        var daysToMonday = ((int)userToday.DayOfWeek - 1 + 7) % 7;
        var weekStart = userToday.AddDays(-daysToMonday);

        weekStart.Should().Be(new DateOnly(expectedYear, expectedMonth, expectedDay));
    }

    [Fact]
    public void SentSlipAlert_Create_SetsFieldsCorrectly()
    {
        var habitId = Guid.NewGuid();
        var weekStart = new DateOnly(2025, 4, 7);

        var alert = SentSlipAlert.Create(habitId, weekStart);

        alert.HabitId.Should().Be(habitId);
        alert.WeekStart.Should().Be(weekStart);
        alert.SentAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DefaultMorningHour_Is8()
    {
        var field = typeof(SlipAlertSchedulerService)
            .GetField("DefaultMorningHour", PrivateStatic)!;
        var value = (int)field.GetValue(null)!;

        value.Should().Be(8);
    }

    private static TimeOnly InvokeCalculateAlertTime(int? peakHour)
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("CalculateAlertTime", PrivateStatic)!;
        return (TimeOnly)method.Invoke(null, [peakHour])!;
    }

    private static bool InvokeIsWithinSendWindow(TimeOnly userTimeNow, TimeOnly alertTime)
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("IsWithinSendWindow", PrivateStatic)!;
        return (bool)method.Invoke(null, [userTimeNow, alertTime])!;
    }
}
