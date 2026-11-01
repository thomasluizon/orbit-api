using System.Globalization;
using System.Text.Json.Nodes;

namespace Orbit.Application.Chat;

internal static class HabitPreviewDates
{
    internal const string DefaultDate = "orbit_default_due_date";

    internal static void Bind(JsonObject source, DateOnly today)
    {
        var oneTime = source["frequency_unit"]?.ToString() is null or "OneTime";
        if (oneTime && source["is_general"]?.ToString() != "true" && source["habit_id"] is null
            && (source["parent_habit_id"] is null || source["frequency_unit"]?.ToString() == "OneTime")
            && source["due_date"] is null)
        {
            source["due_date"] = source["start_date"]?.DeepClone() ?? JsonValue.Create(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            source[DefaultDate] = source["start_date"] is null;
        }
    }
}
