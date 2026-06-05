namespace Orbit.Application.Chat.FeatureExplanations;

/// <summary>
/// A parsed feature explanation: the markdown body plus the frontmatter metadata the
/// assistant surfaces when explaining an Orbit mechanic.
/// </summary>
public record FeatureExplanation(
    string Key,
    string DisplayName,
    IReadOnlyList<string> RelatedCapabilities,
    IReadOnlyList<string> RelatedSurfaces,
    int Version,
    string Body);
