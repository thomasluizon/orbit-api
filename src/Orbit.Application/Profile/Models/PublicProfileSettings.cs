namespace Orbit.Application.Profile.Commands;

/// <summary>Compatibility shape retained until https://github.com/thomasluizon/orbit-tickets/issues/685.</summary>
public record PublicProfileSettings(
    bool Enabled,
    string? Slug,
    string? ShareUrl,
    bool ShowStreak,
    bool ShowLevel,
    bool ShowAchievements,
    bool ShowTopHabits);
