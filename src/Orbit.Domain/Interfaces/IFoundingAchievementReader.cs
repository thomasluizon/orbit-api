namespace Orbit.Domain.Interfaces;

public sealed record FoundingAchievementEvidence(
    bool HasHabitLog,
    bool HasTopLevelHabit,
    bool HasGoal,
    bool HasCompletedGoal,
    bool HasCompletedOnboardingChecklist);

public sealed record FoundingAchievementCursor(Guid UserId, DateTime CreatedAtUtc);

public interface IFoundingAchievementReader
{
    Task<IReadOnlyDictionary<Guid, FoundingAchievementEvidence>> ReadEvidencePageAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    Task<FoundingAchievementEvidence?> ReadEvidenceAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FoundingAchievementCursor>> ReadCandidatePageAsync(
        FoundingAchievementCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);
}
