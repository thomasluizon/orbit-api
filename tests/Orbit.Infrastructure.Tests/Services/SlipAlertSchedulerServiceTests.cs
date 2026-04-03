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

    // --- CalculateAlertTime ---

    [Theory]
    [InlineData(10, 8)]    // 10 - 2 = 8
    [InlineData(12, 10)]   // 12 - 2 = 10
    [InlineData(20, 18)]   // 20 - 2 = 18
    [InlineData(23, 21)]   // 23 - 2 = 21
    [InlineData(9, 8)]     // 9 - 2 = 7, clamped to 8
    [InlineData(8, 8)]     // 8 - 2 = 6, clamped to 8
    [InlineData(5, 8)]     // 5 - 2 = 3, clamped to 8
    public void CalculateAlertTime_WithPeakHour_ReturnsClamped(int peakHour, int expectedHour)
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
        // 24 - 2 = 22 (at clamp max)
        var result = InvokeCalculateAlertTime(24);

        result.Hour.Should().Be(22);
    }

    [Theory]
    [InlineData(0, 8)]    // 0 - 2 = -2, clamped to 8
    [InlineData(1, 8)]    // 1 - 2 = -1, clamped to 8
    [InlineData(2, 8)]    // 2 - 2 = 0, clamped to 8
    [InlineData(3, 8)]    // 3 - 2 = 1, clamped to 8
    public void CalculateAlertTime_VeryEarlyPeakHour_ClampsToMorning(int peakHour, int expectedHour)
    {
        var result = InvokeCalculateAlertTime(peakHour);
        result.Hour.Should().Be(expectedHour);
    }

    [Fact]
    public void CalculateAlertTime_PeakHourAt10_AlertAt8()
    {
        // Exactly at the lower clamp boundary: 10 - 2 = 8
        var result = InvokeCalculateAlertTime(10);
        result.Hour.Should().Be(8);
    }

    [Fact]
    public void CalculateAlertTime_PeakHourAt24_AlertAt22()
    {
        // Exactly at the upper clamp boundary: 24 - 2 = 22
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

    // --- IsWithinSendWindow ---

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
        // Alert at midnight (00:00), user time at 00:02
        var result = InvokeIsWithinSendWindow(new TimeOnly(0, 2), new TimeOnly(0, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_EndOfDay_WorksCorrectly()
    {
        // Alert at 23:55, user time at 23:58
        var result = InvokeIsWithinSendWindow(new TimeOnly(23, 58), new TimeOnly(23, 55));
        result.Should().BeTrue();
    }

    // --- Week-start calculation (replicates RecordSentAlertAsync logic) ---

    [Theory]
    [InlineData(2025, 4, 7, 2025, 4, 7)]   // Monday -> same Monday
    [InlineData(2025, 4, 8, 2025, 4, 7)]   // Tuesday -> previous Monday
    [InlineData(2025, 4, 9, 2025, 4, 7)]   // Wednesday -> previous Monday
    [InlineData(2025, 4, 10, 2025, 4, 7)]  // Thursday -> previous Monday
    [InlineData(2025, 4, 11, 2025, 4, 7)]  // Friday -> previous Monday
    [InlineData(2025, 4, 12, 2025, 4, 7)]  // Saturday -> previous Monday
    [InlineData(2025, 4, 13, 2025, 4, 7)]  // Sunday -> previous Monday
    public void WeekStartCalculation_ReturnsMonday(
        int year, int month, int day,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        // Replicate the week-start formula from RecordSentAlertAsync
        var userToday = new DateOnly(year, month, day);
        var daysToMonday = ((int)userToday.DayOfWeek - 1 + 7) % 7;
        var weekStart = userToday.AddDays(-daysToMonday);

        weekStart.Should().Be(new DateOnly(expectedYear, expectedMonth, expectedDay));
    }

    // --- SentSlipAlert entity ──

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

    // ── DefaultMorningHour constant ──

    [Fact]
    public void DefaultMorningHour_Is8()
    {
        var field = typeof(SlipAlertSchedulerService)
            .GetField("DefaultMorningHour", PrivateStatic)!;
        var value = (int)field.GetValue(null)!;

        value.Should().Be(8);
    }

    // ── Helpers ──

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
