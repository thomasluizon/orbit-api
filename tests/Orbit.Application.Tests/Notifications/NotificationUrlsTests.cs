using System.Reflection;
using FluentAssertions;
using Orbit.Application.Notifications;

namespace Orbit.Application.Tests.Notifications;

public class NotificationUrlsTests
{
    [Fact]
    public void Members_AreAppRelativePathsWithOneLeadingSlash()
    {
        var values = typeof(NotificationUrls)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null) as string)
            .ToList();

        values.Should().NotBeEmpty();
        foreach (var value in values)
        {
            var isAppRelative = value is null
                || value == "/"
                || System.Text.RegularExpressions.Regex.IsMatch(value, "^/[^/]");
            isAppRelative.Should().BeTrue($"{value} must start with exactly one slash");
        }
    }

    [Theory]
    [InlineData(nameof(NotificationUrls.Home), NotificationUrls.Home, "/")]
    [InlineData(nameof(NotificationUrls.Progress), NotificationUrls.Progress, "/progress")]
    [InlineData(nameof(NotificationUrls.Chat), NotificationUrls.Chat, "/chat")]
    [InlineData(nameof(NotificationUrls.Profile), NotificationUrls.Profile, "/profile")]
    [InlineData(nameof(NotificationUrls.CalendarSync), NotificationUrls.CalendarSync, "/calendar-sync")]
    [InlineData(nameof(NotificationUrls.CalendarSyncReview), NotificationUrls.CalendarSyncReview, "/calendar-sync?mode=review")]
    public void Member_HasExpectedRoute(string member, string actual, string expected)
    {
        member.Should().NotBeNullOrWhiteSpace();
        actual.Should().Be(expected);
    }

    [Fact]
    public void WrappedClosedMonth_CarriesAddressablePeriod()
    {
        var url = NotificationUrls.WrappedClosedMonth(2026, 2);

        url.Should().Be("/progress?wrapped=month&year=2026&month=2");
    }
}
