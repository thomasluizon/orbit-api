using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Orbit.Application.Calendar;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Tests.Queries.Calendar;

/// <summary>
/// Pins the one difference between what <c>GET /calendar/events</c> answers and what a suggestion row
/// stores. <c>SourceTimeZone</c> is server-only and <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>
/// is what keeps it off the wire, so that attribute needs a test rather than an argument. An unchanged
/// <c>openapi.json</c> proves nothing here: that file carries no schema for this response at all.
/// </summary>
public class CalendarEventItemSerializationTests
{
    /// <summary>ASP.NET Core serializes a controller response with the web defaults.</summary>
    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ResponseBody_CarriesEndUtcAndOmitsTheSourceTimeZone()
    {
        var json = JsonSerializer.Serialize(LisbonSeries(), ResponseOptions);
        using var response = JsonDocument.Parse(json);

        json.Should().NotContainEquivalentOf("sourceTimeZone");
        json.Should().NotContainEquivalentOf("recurrenceStartUtc");
        json.Should().Contain("endUtc");
        response.RootElement.TryGetProperty("recurrenceTimeZone", out var zone).Should().BeTrue();
        zone.GetString().Should().Be("Europe/Lisbon");
    }

    [Fact]
    public void StoredRow_CarriesTheSourceTimeZoneAndReadsItBack()
    {
        var stored = StoredCalendarEventJson.Serialize(LisbonSeries());

        stored.Should().Contain("Europe/Lisbon");
        var legacy = JsonNode.Parse(stored)!.AsObject();
        legacy.Remove(nameof(CalendarEventItem.RecurrenceTimeZone));

        var readBack = StoredCalendarEventJson.Deserialize(legacy.ToJsonString());

        readBack.Should().NotBeNull();
        readBack!.SourceTimeZone.Should().Be("Europe/Lisbon");
        readBack.RecurrenceTimeZone.Should().Be("Europe/Lisbon");
        readBack.RecurrenceStartUtc.Should().Be(new DateTime(2027, 1, 7, 3, 30, 0, DateTimeKind.Utc));
        readBack.Id.Should().Be("master-lisbon");
        readBack.EndUtc.Should().Be(new DateTime(2027, 1, 7, 4, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "03:30")]
    public void StoredRow_OnlyTimedRecurrencesExposeTheSourceTimeZone(bool isRecurring, string? startTime)
    {
        var item = LisbonSeries() with { IsRecurring = isRecurring, StartTime = startTime };
        var legacy = JsonNode.Parse(StoredCalendarEventJson.Serialize(item))!.AsObject();
        legacy.Remove(nameof(CalendarEventItem.RecurrenceTimeZone));

        var readBack = StoredCalendarEventJson.Deserialize(legacy.ToJsonString());

        readBack.Should().NotBeNull();
        readBack!.SourceTimeZone.Should().Be("Europe/Lisbon");
        readBack.RecurrenceTimeZone.Should().BeNull();
    }

    /// <summary>
    /// A row written before the key existed reads back with no source zone, which the recurrence gate
    /// already treats as unproved. A row whose key holds a number must read back the same way rather
    /// than throw, because the caller's tolerance guard catches only a <see cref="JsonException"/>.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    public void StoredRowWithoutAUsableSourceTimeZone_ReadsBackWithoutOne(string rawValue)
    {
        var stored = JsonNode.Parse(StoredCalendarEventJson.Serialize(LisbonSeries()))!.AsObject();
        stored[nameof(CalendarEventItem.SourceTimeZone)] = JsonNode.Parse(rawValue);

        var readBack = StoredCalendarEventJson.Deserialize(stored.ToJsonString());

        readBack.Should().NotBeNull();
        readBack!.SourceTimeZone.Should().BeNull();
        readBack.Id.Should().Be("master-lisbon");
    }

    private static CalendarEventItem LisbonSeries()
        => new(
            "master-lisbon",
            "Lisbon stand-up",
            null,
            "2027-01-07",
            "03:30",
            "04:00",
            true,
            "RRULE:FREQ=DAILY;BYDAY=TH",
            [],
            StartUtc: new DateTime(2027, 1, 7, 3, 30, 0, DateTimeKind.Utc),
            EndUtc: new DateTime(2027, 1, 7, 4, 0, 0, DateTimeKind.Utc),
            RecurrenceTimeZone: "Europe/Lisbon")
        {
            SourceTimeZone = "Europe/Lisbon",
            RecurrenceStartUtc = new DateTime(2027, 1, 7, 3, 30, 0, DateTimeKind.Utc)
        };
}
