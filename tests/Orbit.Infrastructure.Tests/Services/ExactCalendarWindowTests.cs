using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Orbit.Application.Calendar.Services;
using Orbit.Infrastructure.Services.Calendar;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ExactCalendarWindowTests(ITestOutputHelper output)
{
    [Fact]
    public void InstalledSdkRequestRetainsExactBounds()
    {
        var fromAtUtc = new DateTime(2026, 4, 1, 3, 0, 0, DateTimeKind.Utc);
        var toAtUtc = fromAtUtc.AddDays(1);
        using var service = new CalendarService(new BaseClientService.Initializer { ApplicationName = "Orbit tests" });
        var method = typeof(GoogleCalendarApi).GetMethod("BuildEventsRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        var request = (EventsResource.ListRequest)method.Invoke(null,
            [service, "calendar", null, new CalendarReadWindow(fromAtUtc, toAtUtc)])!;
        request.TimeMinDateTimeOffset.Should().Be(new DateTimeOffset(fromAtUtc));
        request.TimeMaxDateTimeOffset.Should().Be(new DateTimeOffset(toAtUtc));
        request.SingleEvents.Should().BeTrue();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Type = request.GetType().FullName,
            request.TimeMinDateTimeOffset,
            request.TimeMaxDateTimeOffset,
            request.SingleEvents
        }));
    }
}
