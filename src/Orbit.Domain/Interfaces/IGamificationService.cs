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
}
