using System.Text.Json.Serialization;

namespace Orbit.Api.Middleware;

/// <summary>
/// The uniform failure body. The property names are pinned with
/// <see cref="JsonPropertyNameAttribute"/> because they are the wire contract every client
/// reads, and they must not move when the serializer's naming policy changes.
/// <para>
/// <see cref="Args"/> never leaves the server. It carries the values behind any placeholder in
/// <see cref="Error"/> so <see cref="LocalizedErrorResultFilter"/> can format the localized
/// copy for <see cref="ErrorCode"/> with the same values, which the eagerly formatted message
/// alone no longer exposes.
/// </para>
/// </summary>
public sealed record ErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("errorCode")] string? ErrorCode)
{
    [JsonIgnore]
    public IReadOnlyList<object?> Args { get; init; } = Array.Empty<object?>();
}
