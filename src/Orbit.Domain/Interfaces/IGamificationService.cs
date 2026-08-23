using Orbit.Domain.Enums;

namespace Orbit.Domain.Interfaces;

public record HabitLogGamificationResult(int XpEarned, IReadOnlyList<string> NewAchievementIds);

public interface IGamificationService
{
    Task<HabitLogGamificationResult?> ProcessHabitLogged(Guid userId, Guid habitId, CancellationToken ct = default);
    Task<IReadOnlyList<HabitLogGamificationResult>> ProcessHabitsLogged(Guid userId, IReadOnlyList<Guid> habitIds, CancellationToken ct = default);
    Task ProcessHabitCreated(Guid userId, CancellationToken ct = default);
    Task ProcessGoalCreated(Guid userId, CancellationToken ct = default);
    Task ProcessGoalCompleted(Guid userId, Guid goalId, CancellationToken ct = default);
    Task ProcessOnboardingChecklistAsync(Guid userId, OnboardingChecklistSignal signal, CancellationToken ct = default);

    /// <summary>
    /// Repairs missing founding achievements from durable evidence and grants them through the normal
    /// XP funnel. This inline repair persists only missing awards rather than recalculating response
    /// data on every read; once all five awards exist, later reads short-circuit without evidence work.
    /// </summary>
    Task<IReadOnlyList<string>> ReconcileFoundingAchievementsAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Idempotently grants the given achievements to a user, awarding each definition's XP through the
    /// audited funnel, advancing the level, and queuing achievement/level-up notifications. Already-earned
    /// ids are skipped. Returns the ids that were newly granted (empty when all were already earned).
    /// The grant funnel is available to free and Pro users; callers decide which verified ids to request.
    /// </summary>
    Task<IReadOnlyList<string>> TryGrantAchievementsAsync(Guid userId, IReadOnlyList<string> achievementIds, CancellationToken ct = default);
}
