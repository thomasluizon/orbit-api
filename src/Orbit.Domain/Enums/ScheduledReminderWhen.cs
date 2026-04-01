using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Domain.Enums;

/// <summary>
/// When a scheduled reminder fires relative to the habit's due date.
/// Serializes as snake_case ("same_day", "day_before") for backward compatibility.
/// </summary>
[JsonConverter(typeof(ScheduledReminderWhenConverter))]
public enum ScheduledReminderWhen
{
    SameDay,
    DayBefore
}

public class ScheduledReminderWhenConverter : JsonStringEnumConverter<ScheduledReminderWhen>
{
    public ScheduledReminderWhenConverter() : base(JsonNamingPolicy.SnakeCaseLower) { }
}
