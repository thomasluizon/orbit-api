using System.Text.Json;
using FluentAssertions;
using FsCheck.Xunit;
using Orbit.Domain.Entities;
using Orbit.Domain.ValueObjects;
using Orbit.Tests.Generators;

namespace Orbit.Domain.Tests.ValueObjects;

[Properties(Arbitrary = new[] { typeof(OrbitArbitraries) }, MaxTest = 100, Replay = "(30000001,30000017)")]
public class SerializationPropertyTests
{
    private static User NewUser() => User.Create("Property User", "property@example.com").Value;

    [Property]
    public void ChecklistItem_JsonRoundTrips(ChecklistItem item)
    {
        var json = JsonSerializer.Serialize(item);
        var restored = JsonSerializer.Deserialize<ChecklistItem>(json);

        restored.Should().Be(item);
    }

    [Property]
    public void ScheduledReminderTime_JsonRoundTrips(ScheduledReminderTime reminder)
    {
        var json = JsonSerializer.Serialize(reminder);
        var restored = JsonSerializer.Deserialize<ScheduledReminderTime>(json);

        restored.Should().Be(reminder);
    }

    [Property]
    public void SelectedCalendars_NormalizeThenRoundTrip(string[] calendarIds)
    {
        var user = NewUser();
        var expected = calendarIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        user.SetSelectedCalendars(calendarIds);
        var restored = user.GetSelectedCalendarIds();

        if (expected.Count == 0)
            restored.Should().BeNull();
        else
            restored.Should().Equal(expected);
    }
}
