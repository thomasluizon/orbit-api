using System.Globalization;
using System.Text.Json;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

internal static class RepeatReminderToolArguments
{
    public const string Field = "repeat_reminder";

    public static object Schema() => new
    {
        type = JsonSchemaTypes.Object,
        description = "Repeat from the latest dose across midnight. Replaces other reminders. Use only everyday Recurring, Flexible per day, or General habits. Recurring once a day stops after that day is done. The daily target does not stop Flexible or General reminders. When no daily target was requested, use General with no frequency, and do not derive a target from the reminder interval. Keep doctor-prescribed fixed clock times in scheduled_reminders.",
        properties = new
        {
            interval_minutes = new { type = JsonSchemaTypes.Integer, @enum = new[] { 15, 20, 30, 45 }.Concat(Enumerable.Range(1, 23).Select(h => h * 60)).ToArray() },
            active_start = new { type = JsonSchemaTypes.String, description = "Optional local HH:mm; supply active_end too. May cross midnight. Omit both for all day." },
            active_end = new { type = JsonSchemaTypes.String, description = "Optional local HH:mm, paired with active_start." },
            start_at_utc = new { type = JsonSchemaTypes.String, description = "Optional ISO 8601 UTC instant before the first dose. Defaults to when the change is saved." }
        },
        required = new[] { "interval_minutes" },
        additionalProperties = false
    };

    public static string? Validate(JsonElement fields)
    {
        if (!fields.TryGetProperty(Field, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not
            ("interval_minutes" or "active_start" or "active_end" or "start_at_utc")))
            return "repeat_reminder must contain a supported interval and optional active hours and start instant.";
        if (!value.TryGetProperty("interval_minutes", out var minutes) || minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out var interval)
            || !RepeatReminderOptions.IsSupportedInterval(interval))
            return "repeat_reminder.interval_minutes must be 15, 20, 30, 45, or a whole hour from 60 to 1380.";
        if (!TryTime(value, "active_start", out var start) || !TryTime(value, "active_end", out var end)
            || start.HasValue != end.HasValue || start.HasValue && start == end)
            return "repeat_reminder active hours need two different local times in HH:mm format.";
        if (value.TryGetProperty("start_at_utc", out var instant)
            && (instant.ValueKind != JsonValueKind.String || !DateTime.TryParse(instant.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) || parsed.Kind != DateTimeKind.Utc))
            return "repeat_reminder.start_at_utc must be an ISO 8601 UTC instant.";
        return null;
    }

    public static RepeatReminderOptions? Parse(JsonElement fields)
    {
        if (!fields.TryGetProperty(Field, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        TryTime(value, "active_start", out var start);
        TryTime(value, "active_end", out var end);
        return new(value.GetProperty("interval_minutes").GetInt32(), start, end,
            value.TryGetProperty("start_at_utc", out var instant)
                ? DateTime.Parse(instant.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null);
    }

    public static object? Value(RepeatReminderOptions? repeat)
    {
        if (repeat is null)
            return null;
        var value = new Dictionary<string, object> { ["interval_minutes"] = repeat.IntervalMinutes };
        if (repeat.ActiveStart is { } start && repeat.ActiveEnd is { } end)
        {
            value["active_start"] = start.ToString("HH:mm", CultureInfo.InvariantCulture);
            value["active_end"] = end.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        if (repeat.StartAtUtc is { } instant)
            value["start_at_utc"] = instant.ToString("O", CultureInfo.InvariantCulture);
        return value;
    }

    private static bool TryTime(JsonElement value, string field, out TimeOnly? time)
    {
        time = null;
        if (!value.TryGetProperty(field, out var text))
            return true;
        if (text.ValueKind != JsonValueKind.String || !TimeOnly.TryParseExact(text.GetString(), "HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        time = parsed;
        return true;
    }
}
