using System.Reflection;
using System.Text;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.FeatureExplanations;

/// <summary>
/// Loads and parses the embedded feature-explanation markdown bundle so both the chat
/// <c>describe_feature</c> tool and the MCP <c>describe_feature</c> tool can resolve a feature's
/// authoritative explanation and metadata from a single shared source.
/// </summary>
public interface IFeatureExplanationService
{
    IReadOnlyList<string> Keys { get; }
    FeatureExplanation? Get(string key);
}

public class FeatureExplanationService : IFeatureExplanationService
{
    private const string ResourcePrefix = "Orbit.Application.Chat.Content.FeatureExplanations.";
    private const string ResourceSuffix = ".md";

    private readonly IReadOnlyDictionary<string, FeatureExplanation> _byKey;
    private readonly IReadOnlyList<string> _keys;

    public FeatureExplanationService()
    {
        var assembly = typeof(AppConstants).Assembly;
        var byKey = new Dictionary<string, FeatureExplanation>(StringComparer.Ordinal);

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                continue;

            var content = ReadResource(assembly, resourceName);
            var explanation = Parse(content);
            byKey[explanation.Key] = explanation;
        }

        _byKey = byKey;
        _keys = byKey.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Keys => _keys;

    public FeatureExplanation? Get(string key) => _byKey.GetValueOrDefault(key);

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static FeatureExplanation Parse(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidOperationException("Feature explanation is missing a frontmatter block.");

        var closingFence = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closingFence < 0)
            throw new InvalidOperationException("Feature explanation frontmatter is not terminated.");

        var frontmatter = normalized.Substring(4, closingFence - 4);
        var body = normalized[(closingFence + 4)..].TrimStart('\n');

        string? key = null;
        string? displayName = null;
        IReadOnlyList<string> relatedCapabilities = [];
        IReadOnlyList<string> relatedSurfaces = [];
        var version = 0;

        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var separator = rawLine.IndexOf(':');
            if (separator < 0 || rawLine.StartsWith("  ", StringComparison.Ordinal))
                continue;

            var name = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();

            switch (name)
            {
                case "key":
                    key = value;
                    break;
                case "display_name":
                    displayName = value;
                    break;
                case "related_capabilities":
                    relatedCapabilities = ParseInlineList(value);
                    break;
                case "related_surfaces":
                    relatedSurfaces = ParseInlineList(value);
                    break;
                case "version":
                    version = int.TryParse(value, out var parsed) ? parsed : 0;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Feature explanation frontmatter is missing 'key'.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException($"Feature explanation '{key}' frontmatter is missing 'display_name'.");

        return new FeatureExplanation(key, displayName, relatedCapabilities, relatedSurfaces, version, body);
    }

    private static IReadOnlyList<string> ParseInlineList(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
            return [];

        var inner = trimmed[1..^1];
        if (string.IsNullOrWhiteSpace(inner))
            return [];

        return inner
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
