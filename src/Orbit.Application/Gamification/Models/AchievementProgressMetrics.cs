using Orbit.Domain.Enums;

namespace Orbit.Application.Gamification.Models;

/// <summary>
/// A user's current values for every quantifiable achievement metric, loaded once per request. The DTO
/// builders read these to compute each achievement's progress toward its threshold without any
/// per-achievement query.
/// </summary>
public sealed record AchievementProgressMetrics(
    int CurrentStreak,
    int TotalCompletions,
    int GoalsCreated,
    int GoalsCompleted,
    int FriendsCount,
    int CheersSent,
    int EarlyLogs,
    int NightLogs)
{
    public static readonly AchievementProgressMetrics Empty = new(0, 0, 0, 0, 0, 0, 0, 0);

    public int ValueFor(ProgressMetric metric) => metric switch
    {
        ProgressMetric.CurrentStreak => CurrentStreak,
        ProgressMetric.TotalCompletions => TotalCompletions,
        ProgressMetric.GoalsCreated => GoalsCreated,
        ProgressMetric.GoalsCompleted => GoalsCompleted,
        ProgressMetric.FriendsCount => FriendsCount,
        ProgressMetric.CheersSent => CheersSent,
        ProgressMetric.EarlyLogs => EarlyLogs,
        ProgressMetric.NightLogs => NightLogs,
        _ => 0,
    };
}
