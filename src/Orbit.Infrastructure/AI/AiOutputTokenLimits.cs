namespace Orbit.Infrastructure.AI;

/// <summary>
/// Caps include reasoning. Chat retains more than 2,048 tokens beyond the largest captured
/// bulk-create payload (1,189 o200k_base tokens). Bounded auxiliary responses use smaller caps.
/// </summary>
internal static class AiOutputTokenLimits
{
    internal static int ForPurpose(string purpose) => purpose switch
    {
        "chat" or "habit_drafts" => 4096,
        "offer_selection" or "tag_suggestion" or "habit_emoji_inference" => 1024,
        "proactive_item" or "slip_alert" => 512,
        "habit_interpret_date" => 128,
        _ => 2048
    };

    internal static int Resolve(string purpose, int? requested = null)
    {
        if (requested is { } limit)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(requested));
        return Math.Min(requested ?? int.MaxValue, ForPurpose(purpose));
    }
}
