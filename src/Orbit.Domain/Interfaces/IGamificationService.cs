using Orbit.Domain.Enums;

namespace Orbit.Domain.Interfaces;

public record HabitLogGamificationResult(int XpEarned, IReadOnlyList<string> NewAchievementIds);

public interface IGamificationService
{
    Task<HabitLogGamificationResult?> ProcessHabitLogged(Guid userId, Guid habitId, CancellationToken ct = default);
    Task<IReadOnlyList<HabitLogGamificationResult>> ProcessHabitsLogged(Guid userId, IReadOnlyList<Guid> habitIds, CancellationToken ct = default);
    Task ProcessHabitCreated(Guid userId, CancellationToken ct = default);
    Task ProcessGoalCreated(Guid userId, CancellationToken ct = default);
    Task ProcessGoalCompleted(Guid userId, CancellationToken ct = default);
    Task ProcessOnboardingChecklistAsync(Guid userId, OnboardingChecklistSignal signal, CancellationToken ct = default);

    /// <summary>
    /// Idempotently grants the given achievements to a user, awarding each definition's XP through the
    /// audited funnel, advancing the level, and queuing achievement/level-up notifications. Already-earned
    /// ids are skipped. Returns the ids that were newly granted (empty when all were already earned). No
    /// Pro gate — event-driven social/sharing achievements are earned by free users (display stays gated).
    /// </summary>
    Task<IReadOnlyList<string>> TryGrantAchievementsAsync(Guid userId, IReadOnlyList<string> achievementIds, CancellationToken ct = default);
}
