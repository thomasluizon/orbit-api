using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure helper methods of SlipAlertSchedulerService:
/// CalculateAlertTime and IsWithinSendWindow.
/// The main loop and DB-dependent logic are integration concerns.
/// </summary>
public class SlipAlertSchedulerServiceTests
{
    // --- CalculateAlertTime (via reflection since it's private static) ---

    [Theory]
    [InlineData(10, 8)]    // 10 - 2 = 8
    [InlineData(12, 10)]   // 12 - 2 = 10
    [InlineData(20, 18)]   // 20 - 2 = 18
    [InlineData(23, 21)]   // 23 - 2 = 21, but clamped to 22 => actually 21 is fine
    [InlineData(9, 8)]     // 9 - 2 = 7, clamped to 8
    [InlineData(8, 8)]     // 8 - 2 = 6, clamped to 8
    [InlineData(5, 8)]     // 5 - 2 = 3, clamped to 8
    public void CalculateAlertTime_WithPeakHour_ReturnsClamped(int peakHour, int expectedHour)
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("CalculateAlertTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (TimeOnly)method.Invoke(null, [peakHour])!;

        result.Hour.Should().Be(expectedHour);
        result.Minute.Should().Be(0);
    }

    [Fact]
    public void CalculateAlertTime_WithoutPeakHour_ReturnsMorningDefault()
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("CalculateAlertTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (TimeOnly)method.Invoke(null, [null])!;

        result.Hour.Should().Be(8);
        result.Minute.Should().Be(0);
    }

    [Fact]
    public void CalculateAlertTime_HighPeakHour_ClampsTo22()
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("CalculateAlertTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        // 24 - 2 = 22 (at clamp max)
        var result = (TimeOnly)method.Invoke(null, [24])!;

        result.Hour.Should().Be(22);
    }

    // --- IsWithinSendWindow ---

    [Fact]
    public void IsWithinSendWindow_ExactlyAtAlertTime_ReturnsTrue()
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("IsWithinSendWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [new TimeOnly(10, 0), new TimeOnly(10, 0)])!;

        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_WithinFiveMinutes_ReturnsTrue()
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("IsWithinSendWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [new TimeOnly(10, 3), new TimeOnly(10, 0)])!;

        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_ExactlyFiveMinutesAfter_ReturnsFalse()
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("IsWithinSendWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [new TimeOnly(10, 5), new TimeOnly(10, 0)])!;

        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_BeforeAlertTime_ReturnsFalse()
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("IsWithinSendWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [new TimeOnly(9, 58), new TimeOnly(10, 0)])!;

        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_LongAfterAlertTime_ReturnsFalse()
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("IsWithinSendWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (bool)method.Invoke(null, [new TimeOnly(14, 0), new TimeOnly(10, 0)])!;

        result.Should().BeFalse();
    }
}
