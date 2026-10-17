using System.Text.Json.Serialization;

namespace Orbit.Api.Middleware;

public sealed record ErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("errorCode")] string? ErrorCode)
{
    [JsonIgnore]
    public IReadOnlyList<object?> Args { get; init; } = Array.Empty<object?>();
}
