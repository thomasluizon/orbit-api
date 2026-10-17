using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Orbit.Api.OAuth;

/// <summary>
/// How the authorize page writes its two JSON blocks. Accented letters stay readable, and the characters
/// that could end a script element early stay escaped, so neither block can close its own tag.
/// </summary>
public static class OAuthPageJson
{
    /// <summary>The serializer options both blocks use.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>Writes one value as the text content of a JSON script element.</summary>
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
