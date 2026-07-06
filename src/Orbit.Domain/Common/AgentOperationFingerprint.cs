using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit.Domain.Common;

/// <summary>
/// Builds the deterministic dedupe/confirmation key for an agent mutation: the SHA-256 of the
/// operation identity and canonicalized arguments, hex-encoded (64 chars). Hashing keeps
/// arbitrarily large tool payloads inside the fingerprint column's 256-char bound, and
/// canonicalization (ordinal-sorted object keys, compact re-serialization) keeps the key stable
/// across JSON round-trips — pending arguments are persisted as jsonb, which reorders keys and
/// strips whitespace, so the re-executed arguments never match the original raw text byte-for-byte.
/// Every surface that creates or consumes pending agent operations MUST build fingerprints
/// through this helper so confirmation matching stays consistent.
/// </summary>
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
