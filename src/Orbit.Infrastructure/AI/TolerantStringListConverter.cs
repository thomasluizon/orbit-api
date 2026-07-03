using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Infrastructure.AI;

/// <summary>
/// Deserializes a JSON array into a string list while tolerating elements the model returns as
/// objects instead of plain strings (e.g. <c>{"title": "Brush teeth"}</c>). Object elements
/// contribute their <c>title</c>/<c>name</c> property, or their first non-empty string property;
/// elements that yield no usable string are skipped. A non-array token yields an empty list. This
/// keeps LLM JSON that drifts from the requested schema from throwing at the deserialization
/// boundary — https://thomasluizon.sentry.io/issues/ORBIT-API-K
/// </summary>
public sealed class TolerantStringListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return [];
        }

        var results = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var value = ReadElement(ref reader);
            if (!string.IsNullOrWhiteSpace(value))
                results.Add(value.Trim());
        }

        return results;
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }

    private static string? ReadElement(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartObject:
                using (var document = JsonDocument.ParseValue(ref reader))
                    return ExtractFromObject(document.RootElement);
            default:
                reader.Skip();
                return null;
        }
    }

    private static string? ExtractFromObject(JsonElement element)
    {
        string? firstString = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (string.Equals(property.Name, "title", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase))
                return value;

            firstString ??= value;
        }

        return firstString;
    }
}
