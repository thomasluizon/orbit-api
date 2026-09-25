using System.Collections.Frozen;

namespace Orbit.Domain.Common;

/// <summary>
/// The colour-scheme contract after the design decision that collapsed six schemes into one
/// granted warm-orange accent. The API stays append-only for clients that lag behind the Play
/// store: <see cref="AcceptedValues"/> keeps every historical value writable, so an old client's
/// request still succeeds, while the value it carries no longer selects anything.
/// </summary>
public static class ColorSchemes
{
    /// <summary>
    /// The single scheme every account resolves to. Shipped clients map this key to warm orange,
    /// so an account that stored another value renders the granted accent without an app update.
    /// </summary>
    public const string Granted = "orange";

    /// <summary>
    /// The six historical keys, still accepted so an old client's write returns success instead of
    /// a 400. The union narrows only once <c>AppConfig.MinSupportedVersion</c> covers the clients
    /// that send the narrowed set. Frozen rather than an array so no caller can rewrite the rule.
    /// </summary>
    public static readonly FrozenSet<string> AcceptedValues =
        new[] { "purple", "blue", "green", "rose", Granted, "cyan" }.ToFrozenSet(StringComparer.Ordinal);
}
