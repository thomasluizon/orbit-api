using System.Globalization;
using System.Text.Json;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools;

/// <summary>
/// Shared helpers for parsing JsonElement arguments from AI tool calls.
/// Extracted to reduce cognitive complexity (S3776) in individual tool implementations.
/// </summary>
internal static class JsonArgumentParser
{
    public static string? GetOptionalString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    public static int? GetOptionalInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return null;
    }

    public static bool? GetOptionalBool(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    public static FrequencyUnit? ParseFrequencyUnit(JsonElement el)
    {
        var str = GetOptionalString(el, "frequency_unit");
        if (str is null) return null;
        return Enum.TryParse<FrequencyUnit>(str, ignoreCase: true, out var unit) ? unit : null;
    }

    public static List<DayOfWeek>? ParseDays(JsonElement el)
    {
        if (!el.TryGetProperty("days", out var daysEl) || daysEl.ValueKind != JsonValueKind.Array)
            return null;

        var days = new List<DayOfWeek>();
        foreach (var d in daysEl.EnumerateArray())
        {
            var dayStr = d.GetString();
            if (dayStr is not null && Enum.TryParse<DayOfWeek>(dayStr, ignoreCase: true, out var day))
                days.Add(day);
        }
        return days.Count > 0 ? days : null;
    }

    public static DateOnly? ParseDateOnly(JsonElement el, string prop)
    {
        var str = GetOptionalString(el, prop);
        if (str is null) return null;
        return DateOnly.TryParseExact(str, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    public static TimeOnly? ParseTimeOnly(JsonElement el, string prop)
    {
        var str = GetOptionalString(el, prop);
        if (str is null) return null;
        return TimeOnly.TryParse(str, CultureInfo.InvariantCulture, out var time) ? time : null;
    }

    public static List<int>? ParseIntArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<int>();
        foreach (var item in arrEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
                items.Add(item.GetInt32());
        }
        return items.Count > 0 ? items : null;
    }

    public static List<ScheduledReminderTime>? ParseScheduledReminders(JsonElement el)
    {
        if (!el.TryGetProperty("scheduled_reminders", out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<ScheduledReminderTime>();
        foreach (var item in arrEl.EnumerateArray())
        {
            var whenStr = GetOptionalString(item, "when");
            var timeStr = GetOptionalString(item, "time");
            if (whenStr is null || timeStr is null) continue;
            if (!TryParseScheduledReminderWhen(whenStr, out var when)) continue;
            if (!TimeOnly.TryParse(timeStr, CultureInfo.InvariantCulture, out var time)) continue;
            items.Add(new ScheduledReminderTime(when, time));
        }
        return items.Count > 0 ? items : null;
    }

    public static List<ChecklistItem>? ParseChecklistItems(JsonElement el)
    {
        if (!el.TryGetProperty("checklist_items", out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<ChecklistItem>();
        foreach (var item in arrEl.EnumerateArray())
        {
            var text = GetOptionalString(item, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var isChecked = GetOptionalBool(item, "is_checked") ?? false;
            items.Add(new ChecklistItem(text, isChecked));
        }
        return items.Count > 0 ? items : null;
    }

    public static bool TryParseScheduledReminderWhen(string value, out ScheduledReminderWhen result)
    {
        result = value switch
        {
            "same_day" => ScheduledReminderWhen.SameDay,
            "day_before" => ScheduledReminderWhen.DayBefore,
            _ => default
        };
        return value is "same_day" or "day_before";
    }

    /// <summary>
    /// Check if property exists in a JsonElement (regardless of value kind).
    /// </summary>
    public static bool PropertyExists(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out _);

    /// <summary>
    /// Get a string value that may be explicitly null (for "clear" semantics).
    /// Returns string value, null for explicit null, or null for missing.
    /// </summary>
    public static string? GetNullableString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString();
            if (val.ValueKind == JsonValueKind.Null) return null;
        }
        return null;
    }

    public static TimeOnly? ParseTimeOnlyFromString(string str) =>
        TimeOnly.TryParse(str, CultureInfo.InvariantCulture, out var time) ? time : null;
}
