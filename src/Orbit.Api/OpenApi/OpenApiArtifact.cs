using System.Text.Json;

namespace Orbit.Api.OpenApi;

internal sealed record OpenApiArtifact(
    string Id, string HttpVersion, string Revision, string DocumentName, string Path,
    bool Deprecated, string? Replacement)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static IReadOnlyList<OpenApiArtifact> All { get; } = Load();

    private static OpenApiArtifact[] Load()
    {
        using var stream = typeof(OpenApiArtifact).Assembly.GetManifestResourceStream("orbit.openapi.artifacts.json")
            ?? throw new InvalidOperationException("The OpenAPI artifact catalog is required.");
        return JsonSerializer.Deserialize<OpenApiArtifact[]>(stream, SerializerOptions)
            ?? throw new InvalidOperationException("The OpenAPI artifact catalog must contain an array.");
    }
}
