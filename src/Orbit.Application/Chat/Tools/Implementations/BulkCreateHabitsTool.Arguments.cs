using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Application.Chat.Tools.Implementations;

public partial class BulkCreateHabitsTool
{
    internal JsonElement PrepareArguments(JsonElement arguments)
    {
        var root = JsonNode.Parse(OptionalToolArguments.OmitOptionalNulls(arguments, GetParameterSchema()).GetRawText())!;
        if (root is JsonObject obj && obj["habits"] is JsonArray habits)
            PrepareItems(habits);
        return JsonSerializer.SerializeToElement(root);
    }

    private static void PrepareSchemaItems(JsonArray items)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            item.Remove("preview_item_id");
            item.Remove(HabitHeadingParents.CandidateIdsField);
            item.Remove(HabitHeadingParents.SuggestedParentField);
            item.Remove(HabitHeadingParents.ExistingCandidatesField);
            if (item.ContainsKey("habit_id")) item.Remove("clear_fields");
            if (item["sub_habits"] is JsonArray children) PrepareSchemaItems(children);
        }
    }

    private static void PrepareItems(JsonArray items)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            foreach (var field in new[] { "due_time", "due_end_time" })
                if (item[field] is JsonValue value && value.TryGetValue<string>(out var text)
                    && TimeOnly.TryParseExact(text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    && time.Second == 0)
                    item[field] = time.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (item["habit_id"] is null && item["due_date"] is null
                && item["frequency_unit"] is JsonValue unit && unit.TryGetValue<string>(out var frequency)
                && frequency is "Day" or "Week" or "Month" or "Year"
                && item["start_date"] is { } start)
                item["due_date"] = start.DeepClone();
            if (item["sub_habits"] is JsonArray children)
                PrepareItems(children);
        }
    }
}
