namespace Orbit.Domain.Common;

/// <summary>
/// The colour-scheme contract after the design decision that collapsed six schemes into one
/// granted warm-orange accent. The API stays append-only for clients that lag behind the Play
/// store: <see cref="AcceptedValues"/> keeps every historical value writable and stored, while
/// every read path reports <see cref="Granted"/> instead of the stored value.
/// </summary>
public static class ColorSchemes
{
    /// <summary>
    /// The single scheme every account resolves to. Shipped clients map this key to warm orange,
    /// so an account that stored another value renders the granted accent without an app update.
    /// </summary>
    public const string Granted = "orange";

    /// <summary>
    /// The six historical keys. They stay accepted so an old client's write still succeeds; the
    /// union narrows only once <c>AppConfig.MinSupportedVersion</c> covers the narrowed clients.
    /// </summary>
    public static readonly string[] AcceptedValues = ["purple", "blue", "green", "rose", Granted, "cyan"];
}
