namespace Orbit.Domain.Enums;

/// <summary>
/// The countable, linearly-accumulating user metric an achievement advances toward its threshold.
/// <see cref="None"/> covers boolean/one-shot and expensive-consecutive-scan achievements that have no
/// cheap linear progress (e.g. first-habit, perfect-week) — those expose no progress bar.
/// </summary>
public enum ProgressMetric
{
    None,
    CurrentStreak,
    TotalCompletions,
    GoalsCreated,
    GoalsCompleted,
    FriendsCount,
    CheersSent,
    EarlyLogs,
    NightLogs
}
