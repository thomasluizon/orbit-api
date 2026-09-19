using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar;

/// <summary>
/// Reads and writes the JSON a <c>GoogleCalendarSyncSuggestion</c> row keeps in
/// <c>RawEventJson</c>. That column is server-owned storage rather than a client contract, so it
/// carries one key the response never does: the source calendar's own timezone, which the recurrence
/// gate needs and which <see cref="CalendarEventItem.SourceTimeZone"/> hides from the response body.
/// Without it a stored row would reach the suggestion feed with less evidence than the events feed
/// had, and the two feeds would answer differently about one series.
/// </summary>
/// <remarks>
/// The shape stays flat and additive, so a row written before the key existed still reads back as a
/// plain <see cref="CalendarEventItem"/> with a null source timezone, which the gate treats as
/// unproved.
/// </remarks>
internal static class StoredCalendarEventJson
{
    private const string SourceTimeZoneKey = nameof(CalendarEventItem.SourceTimeZone);

    internal static string Serialize(CalendarEventItem item)
    {
        var stored = JsonSerializer.SerializeToNode(item)!.AsObject();
        stored[SourceTimeZoneKey] = item.SourceTimeZone;
        return stored.ToJsonString();
    }

    internal static CalendarEventItem? Deserialize(string rawEventJson)
    {
        if (JsonNode.Parse(rawEventJson) is not JsonObject stored)
            return null;

        var item = stored.Deserialize<CalendarEventItem>();
        if (item is null)
            return null;

        return item with { SourceTimeZone = ReadSourceTimeZone(stored) };
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
}
