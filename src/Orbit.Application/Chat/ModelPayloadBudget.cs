using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Application.Common;

namespace Orbit.Application.Chat;

internal static class ModelPayloadBudget
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool Fits(object payload, int reservedCharacters = 0) =>
        JsonSerializer.Serialize(payload, SerializerOptions).Length <= AppConstants.MaxAiToolPayloadJsonLength - reservedCharacters;

    public static string ShortName(string name, int maxLength = 64)
    {
        if (name.Length <= maxLength) return name;
        var length = maxLength > 0 && char.IsHighSurrogate(name[maxLength - 1]) ? maxLength - 1 : maxLength;
        return name[..length];
    }
}
