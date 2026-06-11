using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Application.Chat.Commands;

namespace Orbit.Application.Chat.Models;

/// <summary>
/// One server-sent event in the chat stream contract shared with the clients.
/// started/round/delta/reset report progress, final carries the complete ChatResponse,
/// and error carries an HTTP-equivalent status plus the same error/code shape the
/// buffered endpoint returns. Serialized camelCase with enums as strings, mirroring
/// the regular API responses. Only the envelope fields skip nulls — the nested
/// ChatResponse must serialize null fields exactly like the buffered endpoint does,
/// because the shared client schema treats them as part of the contract.
/// </summary>
public sealed record ChatStreamEvent(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Iteration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatResponse? Response = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Status = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null)
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
    public static ChatStreamEvent Final(ChatResponse response) => new("final", Response: response);

    public static ChatStreamEvent Failure(int status, string error, string? code = null) =>
        new("error", Status: status, Error: error, Code: code);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
