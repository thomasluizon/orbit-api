using FluentAssertions;
using Orbit.Application.Calendar.Services;

namespace Orbit.Application.Tests.Queries.Calendar;

public class LegacyCalendarMatchKeyTests
{
    [Theory]
    [InlineData("Daily standup", null, "09:00", "daily standup||09:00")]
    [InlineData("Daily standup", "2027-01-07", null, "daily standup|2027-01-07|")]
    [InlineData("Daily standup", null, null, "daily standup||")]
    [InlineData(" \tDäIlY  STANDUP\r\n", "2027-01-07", "09:00", "däily  standup|2027-01-07|09:00")]
    public void BuildLegacyMatchKey_PreservesExistingIdentity(
        string title, string? startDate, string? startTime, string expected)
    {
        LegacyCalendarMatchKey.BuildLegacyMatchKey(title, startDate, startTime).Should().Be(expected);
    }
}
