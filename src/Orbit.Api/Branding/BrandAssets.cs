using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Orbit.Api.Branding;

internal static class BrandAssets
{
    internal const int CacheSeconds = 604800;

    private static readonly FrozenDictionary<string, (byte[] Content, string ContentType)> Images =
        new Dictionary<string, (byte[], string)>(StringComparer.Ordinal)
        {
            ["/oauth/assets/google-mark.svg"] = Read("google-mark.svg", "image/svg+xml"),
            ["/favicon.ico"] = Read("favicon.ico", "image/x-icon"),
            ["/favicon.svg"] = Read("favicon.svg", "image/svg+xml"),
            ["/apple-touch-icon.png"] = Read("apple-touch-icon.png", "image/png"),
            ["/orbit-icon-512.png"] = Read("orbit-icon-512.png", "image/png"),
            ["/bimi/orbit.svg"] = Read("orbit-bimi.svg", "image/svg+xml")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static string SvgStyleHash { get; } = ComputeSvgStyleHash();

    private static string ComputeSvgStyleHash()
    {
        var svg = XDocument.Parse(Encoding.UTF8.GetString(Images["/favicon.svg"].Content));
        var style = svg.Descendants(XName.Get("style", "http://www.w3.org/2000/svg")).Single().Value;
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(style)));
    }

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
