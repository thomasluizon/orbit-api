namespace Orbit.Application.Gamification;

/// <summary>
/// The whitelist of client-reportable event keys and the achievement each grants. Only keys present here
/// can be turned into a grant by the report-event endpoint; anything else is rejected at validation. The
/// user id always comes from the auth token, never the request body, and grants are idempotent.
/// </summary>
public static class AchievementEventMap
{
    public const string CardShared = "card_shared";
    public const string WrappedViewed = "wrapped_viewed";

    public static IReadOnlyDictionary<string, string> ClientReportable { get; } =
        new Dictionary<string, string>
        {
            [CardShared] = AchievementDefinitions.ShowOff,
            [WrappedViewed] = AchievementDefinitions.YearInReview,
        };

    public static bool IsKnown(string? eventKey) =>
        eventKey is not null && ClientReportable.ContainsKey(eventKey);
}
