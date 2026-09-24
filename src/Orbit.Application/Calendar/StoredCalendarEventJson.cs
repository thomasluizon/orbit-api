using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar;

/// <summary>
/// Reads and writes the JSON a <c>GoogleCalendarSyncSuggestion</c> row keeps in
/// <c>RawEventJson</c>. That column is server-owned storage rather than a client contract, so it
/// carries the source calendar's timezone, recurrence-defined start, and expanded starts outside the
/// response. Both feeds need these facts to judge one series the same way.
/// </summary>
/// <remarks>
/// The shape stays flat and additive. Missing keys on a legacy row leave its recurrence unproved.
/// </remarks>
internal static class StoredCalendarEventJson
{
    private const string SourceTimeZoneKey = nameof(CalendarEventItem.SourceTimeZone);
    private const string RecurrenceStartUtcKey = nameof(CalendarEventItem.RecurrenceStartUtc);
    private const string ExpandedOccurrencesUtcKey = nameof(CalendarEventItem.ExpandedOccurrencesUtc);

    internal static string Serialize(CalendarEventItem item)
    {
        var stored = JsonSerializer.SerializeToNode(item)!.AsObject();
        stored[SourceTimeZoneKey] = item.SourceTimeZone;
        stored[RecurrenceStartUtcKey] = item.RecurrenceStartUtc;
        stored[ExpandedOccurrencesUtcKey] = JsonSerializer.SerializeToNode(item.ExpandedOccurrencesUtc);
        return stored.ToJsonString();
    }

    internal static CalendarEventItem? Deserialize(string rawEventJson)
    {
        if (JsonNode.Parse(rawEventJson) is not JsonObject stored)
            return null;

        var item = stored.Deserialize<CalendarEventItem>();
        if (item is null)
            return null;

        return item with
        {
            SourceTimeZone = ReadSourceTimeZone(stored),
            RecurrenceStartUtc = ReadRecurrenceStartUtc(stored),
            ExpandedOccurrencesUtc = ReadExpandedOccurrencesUtc(stored)
        };
    }

    internal static bool NeedsRecurrenceEvidenceRefresh(string rawEventJson)
    {
        try
        {
            return Deserialize(rawEventJson)?.NeedsRecurrenceEvidenceRefresh ?? true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads the key only when it really holds a string. <c>GetValue&lt;string&gt;</c> throws
    /// <see cref="InvalidOperationException"/> on a number, which <c>DeserializeEvent</c> does not
    /// catch, so one such row would fail the whole suggestion request instead of itself. A value of
    /// any other kind is no evidence of a source zone, which is what a missing key already means and
    /// what the recurrence gate already handles by withholding the series.
    /// </summary>
    private static string? ReadSourceTimeZone(JsonObject stored)
        => stored[SourceTimeZoneKey] is JsonValue value && value.TryGetValue<string>(out var timeZoneId)
            ? timeZoneId
            : null;

    private static DateTime? ReadRecurrenceStartUtc(JsonObject stored)
        => stored[RecurrenceStartUtcKey] is JsonValue value
            && value.TryGetValue<DateTime>(out var startUtc)
            ? startUtc
            : null;

    private static IReadOnlyList<DateTime>? ReadExpandedOccurrencesUtc(JsonObject stored)
    {
        try
        {
            return stored[ExpandedOccurrencesUtcKey]?.Deserialize<List<DateTime>>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
