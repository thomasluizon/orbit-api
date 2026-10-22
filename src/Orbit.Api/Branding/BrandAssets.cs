using System.Collections.Frozen;

namespace Orbit.Api.Branding;

internal static class BrandAssets
{
    internal const int CacheSeconds = 604800;

    private static readonly FrozenDictionary<string, (byte[] Content, string ContentType)> Images =
        new Dictionary<string, (byte[], string)>(StringComparer.Ordinal)
        {
            ["/favicon.ico"] = Read("favicon.ico", "image/x-icon"),
            ["/favicon.svg"] = Read("favicon.svg", "image/svg+xml"),
            ["/apple-touch-icon.png"] = Read("apple-touch-icon.png", "image/png"),
            ["/orbit-icon-512.png"] = Read("orbit-icon-512.png", "image/png")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static (byte[] Content, string ContentType)? TryRead(string path) =>
        Images.TryGetValue(path, out var image) ? image : null;

    private static (byte[], string) Read(string name, string contentType)
    {
        using var stream = typeof(BrandAssets).Assembly.GetManifestResourceStream($"orbit.brand.{name}")
            ?? throw new InvalidOperationException($"Embedded brand asset {name} is missing from the assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return (buffer.ToArray(), contentType);
    }
}
