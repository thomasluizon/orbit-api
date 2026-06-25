using FluentAssertions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class TimeFormatResolverTests
{
    [Theory]
    [InlineData("America/Sao_Paulo")]
    [InlineData("Europe/Paris")]
    [InlineData("Europe/London")]
    [InlineData("Asia/Tokyo")]
    [InlineData("America/Argentina/Buenos_Aires")]
    public void Uses24HourClock_ReturnsTrue_For24HourRegions(string timeZone)
    {
        TimeFormatResolver.Uses24HourClock(timeZone).Should().BeTrue();
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("America/Los_Angeles")]
    [InlineData("America/Chicago")]
    [InlineData("America/Toronto")]
    [InlineData("Australia/Sydney")]
    [InlineData("Pacific/Auckland")]
    [InlineData("Asia/Kolkata")]
    [InlineData("Asia/Manila")]
    public void Uses24HourClock_ReturnsFalse_For12HourRegions(string timeZone)
    {
        TimeFormatResolver.Uses24HourClock(timeZone).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    public void Uses24HourClock_DefaultsToTrue_ForNullOrUnknown(string? timeZone)
    {
        TimeFormatResolver.Uses24HourClock(timeZone).Should().BeTrue();
    }
}
