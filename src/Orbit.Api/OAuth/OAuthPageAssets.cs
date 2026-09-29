using System.Collections.Frozen;
using System.Reflection;

namespace Orbit.Api.OAuth;

/// <summary>
/// The self-hosted assets the authorize page needs. The three latin webfont subsets are embedded in the
/// assembly and served from <c>/oauth/assets/{name}</c>, so the page loads no third-party font host, and
/// the name is matched against this closed map rather than resolved as a path.
/// </summary>
public static class OAuthPageAssets
{
    /// <summary>The cache lifetime a content-addressed font takes: the name changes before the bytes do.</summary>
    public const int CacheSeconds = 31536000;

    private const string FontContentType = "font/woff2";

    private static readonly FrozenDictionary<string, string> EmbeddedFonts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["geist-latin.woff2"] = "orbit.oauth.geist-latin.woff2",
        ["geist-mono-latin.woff2"] = "orbit.oauth.geist-mono-latin.woff2",
        ["space-grotesk-latin.woff2"] = "orbit.oauth.space-grotesk-latin.woff2"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The asset names the page is allowed to request, in the order the stylesheet declares them.</summary>
    public static IReadOnlyCollection<string> Names => EmbeddedFonts.Keys;

    /// <summary>
    /// Reads one allowlisted asset. Returns <see langword="null"/> for any name outside the map, so an
    /// unknown or traversing name never reaches the resource loader.
    /// </summary>
    public static (byte[] Content, string ContentType)? TryRead(string name)
    {
        if (!EmbeddedFonts.TryGetValue(name, out var resourceName))
            return null;

        using var stream = typeof(OAuthPageAssets).GetTypeInfo().Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded OAuth asset {resourceName} is missing from the assembly.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return (buffer.ToArray(), FontContentType);
    }
}
