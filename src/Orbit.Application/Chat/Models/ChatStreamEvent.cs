using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Application.Chat.Commands;

namespace Orbit.Application.Chat.Models;

public sealed record ChatStreamEvent(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Iteration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatResponse? Response = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Status = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Domain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Access = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ChatStreamEvent Started() => new("started");
    public static ChatStreamEvent Round(int iteration) => new("round", Iteration: iteration);
    public static ChatStreamEvent Delta(string text) => new("delta", Text: text);
    public static ChatStreamEvent Reset() => new("reset");
    public static ChatStreamEvent Step(string domain, string access) => new("step", Domain: domain, Access: access);
    public static ChatStreamEvent Final(ChatResponse response) => new("final", Response: response);

    public static ChatStreamEvent Failure(int status, string error, string? code = null) =>
        new("error", Status: status, Error: error, Code: code);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
