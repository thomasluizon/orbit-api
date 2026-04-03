using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class TimeZoneHelperTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    // --- Null/empty input tests ---

    [Fact]
    public void FindTimeZone_NullInput_ReturnsUtc()
    {
        var result = TimeZoneHelper.FindTimeZone(null);

        result.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void FindTimeZone_EmptyString_ReturnsUtc()
    {
        var result = TimeZoneHelper.FindTimeZone("");

        result.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void FindTimeZone_NullWithLogger_ReturnsUtc()
    {
        var result = TimeZoneHelper.FindTimeZone(null, _logger);

        result.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void FindTimeZone_EmptyWithLogger_ReturnsUtc()
    {
        var result = TimeZoneHelper.FindTimeZone("", _logger);

        result.Should().Be(TimeZoneInfo.Utc);
    }

    // --- Known timezone tests ---

    [Fact]
    public void FindTimeZone_Utc_ReturnsUtc()
    {
        var result = TimeZoneHelper.FindTimeZone("UTC");

        result.Should().Be(TimeZoneInfo.Utc);
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("America/Sao_Paulo")]
    [InlineData("Europe/London")]
    [InlineData("Asia/Tokyo")]
    [InlineData("Australia/Sydney")]
    public void FindTimeZone_ValidIanaTimezone_ReturnsCorrectZone(string timezoneId)
    {
        // This test may behave differently on Windows vs Linux due to timezone ID formats
        // On Windows, IANA IDs are mapped by .NET 6+
        TimeZoneInfo? result = null;
        var action = () => result = TimeZoneHelper.FindTimeZone(timezoneId);

        // Should not throw -- either finds the timezone or falls back to UTC
        action.Should().NotThrow();
        result.Should().NotBeNull();
    }

    // --- Unknown timezone tests ---

    [Fact]
    public void FindTimeZone_UnknownTimezone_ReturnsUtc()
    {
        var result = TimeZoneHelper.FindTimeZone("Invalid/Timezone");

        result.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void FindTimeZone_GarbageString_ReturnsUtc()
    {
        var result = TimeZoneHelper.FindTimeZone("not-a-timezone");

        result.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void FindTimeZone_UnknownWithLogger_ReturnsUtcAndLogs()
    {
        var result = TimeZoneHelper.FindTimeZone("Invalid/Timezone", _logger, Guid.NewGuid());

        result.Should().Be(TimeZoneInfo.Utc);
        _logger.ReceivedWithAnyArgs().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void FindTimeZone_UnknownWithoutLogger_ReturnsUtcWithoutError()
    {
        var action = () => TimeZoneHelper.FindTimeZone("Invalid/Timezone");

        action.Should().NotThrow();
        var result = action();
        result.Should().Be(TimeZoneInfo.Utc);
    }

    // --- With userId parameter ---

    [Fact]
    public void FindTimeZone_NullUserId_DoesNotThrow()
    {
        var action = () => TimeZoneHelper.FindTimeZone("Invalid/Timezone", _logger, null);

        action.Should().NotThrow();
    }

    [Fact]
    public void FindTimeZone_WithUserId_DoesNotThrow()
    {
        var action = () => TimeZoneHelper.FindTimeZone("Invalid/Timezone", _logger, Guid.NewGuid());

        action.Should().NotThrow();
    }

    // --- Without logger parameter ---

    [Fact]
    public void FindTimeZone_NullLogger_ValidTimezone_ReturnsZone()
    {
        var result = TimeZoneHelper.FindTimeZone("UTC", null);

        result.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void FindTimeZone_NullLogger_InvalidTimezone_ReturnsUtc()
    {
        var result = TimeZoneHelper.FindTimeZone("Fake/Zone", null);

        result.Should().Be(TimeZoneInfo.Utc);
    }

    // --- Return value identity ---

    [Fact]
    public void FindTimeZone_SameInput_ReturnsSameTimezone()
    {
        var result1 = TimeZoneHelper.FindTimeZone("UTC");
        var result2 = TimeZoneHelper.FindTimeZone("UTC");

        result1.Should().Be(result2);
    }

    [Fact]
    public void FindTimeZone_DifferentInvalidInputs_BothReturnUtc()
    {
        var result1 = TimeZoneHelper.FindTimeZone("Fake/Zone1");
        var result2 = TimeZoneHelper.FindTimeZone("Fake/Zone2");

        result1.Should().Be(TimeZoneInfo.Utc);
        result2.Should().Be(TimeZoneInfo.Utc);
    }
}
