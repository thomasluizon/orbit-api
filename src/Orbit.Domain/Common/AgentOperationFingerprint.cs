using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit.Domain.Common;

public static class AgentOperationFingerprint
{
    public static string Compute(string operationIdentity, string argumentsJson)
    {
        var canonicalArguments = Canonicalize(argumentsJson);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationIdentity}:{canonicalArguments}"));
        return Convert.ToHexString(bytes);
    }

    private static string Canonicalize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
