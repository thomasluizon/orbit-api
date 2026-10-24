using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orbit.Application.Chat.Commands;

namespace Orbit.Application.Chat;

public static class ChatTurnIdentity
{
    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string Fingerprint(ProcessUserChatCommand command)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            command.Message,
            command.ImageData,
            command.ImageMimeType,
            command.History,
            command.ClientContext,
            command.ConfirmationToken,
            command.BlockAnswer,
            command.AuthMethod,
            command.IsReadOnlyCredential,
            GrantedScopes = command.GrantedScopes?.Order(StringComparer.Ordinal).ToArray(),
            command.EvalTraceRequested
        });
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, payload);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else
            element.WriteTo(writer);
    }
}
