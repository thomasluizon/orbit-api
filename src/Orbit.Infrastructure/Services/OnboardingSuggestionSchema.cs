using System.Text.Json;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

internal static class OnboardingSuggestionSchema
{
    internal const int MaxOutputTokens = 1024;
    internal const string Json = """
        {"type":"object","additionalProperties":false,"properties":{
        "emoji":{"type":["string","null"],"maxLength":16},
        "frequencyUnit":{"type":["string","null"],"enum":["Day","Week","Month","Year",null]},
        "frequencyQuantity":{"type":["integer","null"],"minimum":1,"maximum":365},
        "days":{"type":"array","maxItems":7,"items":{"type":"string","enum":["Sunday","Monday","Tuesday","Wednesday","Thursday","Friday","Saturday"]}},
        "isFlexible":{"type":"boolean"},
        "flexibleTarget":{"type":["integer","null"],"minimum":1,"maximum":366},
        "dueTime":{"type":["string","null"],"pattern":"^([01][0-9]|2[0-3]):[0-5][0-9]$"},
        "subHabits":{"type":"array","maxItems":6,"items":{"type":"string","minLength":1,"maxLength":100}},
        "checklistItems":{"type":"array","maxItems":6,"items":{"type":"string","minLength":1,"maxLength":100}}},
        "required":["emoji","frequencyUnit","frequencyQuantity","days","isFlexible","flexibleTarget","dueTime","subHabits","checklistItems"]}
        """;
    private static readonly string[] Fields = ["emoji", "frequencyUnit", "frequencyQuantity", "days",
        "isFlexible", "flexibleTarget", "dueTime", "subHabits", "checklistItems"];

    internal static HabitSetupSuggestion? Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != Fields.Length || properties.Distinct(StringComparer.Ordinal).Count() != Fields.Length
            || properties.Any(property => !Fields.Contains(property, StringComparer.Ordinal)))
            return null;
        var frequency = String(root.GetProperty("frequencyUnit"));
        if (frequency is not null && !Enum.GetNames<FrequencyUnit>().Contains(frequency, StringComparer.Ordinal))
            return null;
        var days = Strings(root.GetProperty("days"));
        if (days.Any(day => !Enum.GetNames<DayOfWeek>().Contains(day, StringComparer.Ordinal)))
            return null;
        var suggestion = new HabitSetupSuggestion(String(root.GetProperty("emoji")),
            frequency is null ? null : Enum.Parse<FrequencyUnit>(frequency),
            Integer(root.GetProperty("frequencyQuantity")), days.Select(Enum.Parse<DayOfWeek>).ToArray(),
            root.GetProperty("isFlexible").GetBoolean(), Integer(root.GetProperty("flexibleTarget")),
            String(root.GetProperty("dueTime")), Strings(root.GetProperty("subHabits")), Strings(root.GetProperty("checklistItems")));
        return HabitSetupSuggestionGuard.IsValid(suggestion) ? suggestion : null;
    }

    private static string? String(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    private static int? Integer(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    private static string[] Strings(JsonElement value) => value.EnumerateArray()
        .Select(item => item.GetString() ?? throw new JsonException("Invalid schedule string.")).ToArray();
}
