namespace Orbit.Application.Uploads.Common;

public static class UploadContentTypes
{
    public const long MaxSizeBytes = 8 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> ExtensionByContentType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = "png",
            ["image/jpeg"] = "jpg",
            ["image/webp"] = "webp",
        };

    public static IReadOnlyCollection<string> Allowed => (IReadOnlyCollection<string>)ExtensionByContentType.Keys;

    public static bool IsAllowed(string? contentType) =>
        contentType is not null && ExtensionByContentType.ContainsKey(contentType);

    public static string ExtensionFor(string contentType) => ExtensionByContentType[contentType];
}
