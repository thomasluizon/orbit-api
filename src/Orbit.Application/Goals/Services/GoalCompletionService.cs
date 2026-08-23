using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Services;

public readonly record struct GoalCompletionUpdate(
    Guid GoalId,
    string Title,
    decimal CurrentValue,
    decimal TargetValue,
    bool JustCompleted);

public interface IGoalCompletionService
{
    Task SaveCompletedGoalAsync(
        Guid userId,
        Guid goalId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoalCompletionUpdate>> SyncDerivedGoalsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> goalIds,
        DateOnly userToday,
        bool passiveSync = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the single persistence path for goal completion and its rewards. Derived inputs are loaded
/// in one untracked split-query batch, then retained as value snapshots because gamification
/// concurrency retries can reset the shared change tracker. Each candidate is resolved from its
/// identifier inside its own retryable transaction, so a reset cannot detach a later goal that the
/// operation still intends to save.
/// </summary>
public sealed class GoalCompletionService(
    IGenericRepository<Goal> goalRepository,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork) : IGoalCompletionService
{
    public Task SaveCompletedGoalAsync(
        Guid userId,
        Guid goalId,
        CancellationToken cancellationToken = default)
    {
        return unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var wasAlreadyCompleted = await goalRepository.AnyAsync(
                g => g.Id == goalId && g.UserId == userId && g.Status == GoalStatus.Completed,
                transactionToken);
            if (wasAlreadyCompleted)
                return;

            await unitOfWork.SaveChangesAsync(transactionToken);
            await AwardCompletedGoalAsync(userId, goalId, transactionToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<GoalCompletionUpdate>> SyncDerivedGoalsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> goalIds,
        DateOnly userToday,
        bool passiveSync = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goalIds);

        var candidateIds = goalIds.Distinct().ToList();
        if (candidateIds.Count == 0)
            return [];

        if (passiveSync)
            return await SyncDerivedBatchAsync(userId, candidateIds, userToday, passiveSync, cancellationToken);

        return await unitOfWork.ExecuteInTransactionAsync<IReadOnlyList<GoalCompletionUpdate>>(async transactionToken =>
        {
            await unitOfWork.SaveChangesAsync(transactionToken);
            return await SyncDerivedBatchAsync(userId, candidateIds, userToday, passiveSync, transactionToken);
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<GoalCompletionUpdate>> SyncDerivedBatchAsync(
        Guid userId,
        IReadOnlyCollection<Guid> candidateIds,
        DateOnly userToday,
        bool passiveSync,
        CancellationToken cancellationToken)
    {
        var candidates = await goalRepository.FindAsync(
            goal => candidateIds.Contains(goal.Id) && goal.UserId == userId,
            query => query.Include(goal => goal.Habits).ThenInclude(habit => habit.Logs),
            cancellationToken);
        var snapshots = candidates
            .Select(goal => CreateSnapshot(goal, userToday, passiveSync))
            .Where(snapshot => snapshot.HasValue)
            .Select(snapshot => snapshot!.Value)
            .ToList();

        var updates = new List<GoalCompletionUpdate>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var update = await SyncCandidateAsync(userId, snapshot, cancellationToken);
            if (update.HasValue)
                updates.Add(update.Value);
        }

        return updates;
    }

    private readonly record struct DerivedGoalSnapshot(
        Guid GoalId,
        GoalType Type,
        decimal CurrentValue,
        bool ResetStreak);

    private static DerivedGoalSnapshot? CreateSnapshot(Goal goal, DateOnly userToday, bool passiveSync)
    {
        if (goal.Status != GoalStatus.Active)
            return null;

        if (goal.Type == GoalType.Standard)
        {
            return goal.Habits.Count == 0
                ? null
                : new DerivedGoalSnapshot(
                    goal.Id,
                    goal.Type,
                    GoalProgressSyncService.CalculateStandardCompletions(goal),
                    ResetStreak: false);
        }

        if (passiveSync && !GoalStreakSyncService.NeedsPassiveSync(goal, userToday))
            return null;

        if (goal.Habits.Count == 0)
        {
            var shouldReset = goal.CurrentValue != 0 || (!passiveSync && goal.StreakSyncedAtUtc is not null);
            return shouldReset
                ? new DerivedGoalSnapshot(goal.Id, goal.Type, CurrentValue: 0, ResetStreak: true)
                : null;
        }

        var currentStreak = GoalStreakSyncService.CalculateCurrentStreak(goal, userToday);
        return currentStreak.HasValue
            ? new DerivedGoalSnapshot(goal.Id, goal.Type, currentStreak.Value, ResetStreak: false)
            : null;
    }

    private Task<GoalCompletionUpdate?> SyncCandidateAsync(
        Guid userId,
        DerivedGoalSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        return unitOfWork.ExecuteInTransactionAsync<GoalCompletionUpdate?>(async transactionToken =>
        {
            var goal = await goalRepository.FindOneTrackedAsync(
                candidate => candidate.Id == snapshot.GoalId && candidate.UserId == userId,
                snapshot.Type == GoalType.Standard ? query => query.Include(candidate => candidate.Habits) : null,
                transactionToken);
            if (goal is null)
                return null;

            if (goal.Status == GoalStatus.Completed)
            {
                return new GoalCompletionUpdate(
                    goal.Id,
                    goal.Title,
                    goal.CurrentValue,
                    goal.TargetValue,
                    JustCompleted: false);
            }

            var outcome = ApplySnapshot(goal, snapshot);
            if (!outcome.Synced)
                return null;

            var update = new GoalCompletionUpdate(
                goal.Id,
                goal.Title,
                goal.CurrentValue,
                goal.TargetValue,
                outcome.JustCompleted);

            await unitOfWork.SaveChangesAsync(transactionToken);
            if (outcome.JustCompleted)
                await AwardCompletedGoalAsync(userId, goal.Id, transactionToken);

            return update;
        }, cancellationToken);
    }

    private static GoalProgressSyncOutcome ApplySnapshot(Goal goal, DerivedGoalSnapshot snapshot)
    {
        if (snapshot.ResetStreak)
            return new GoalProgressSyncOutcome(goal.ResetStreakProgress(), JustCompleted: false);

        var result = snapshot.Type == GoalType.Streak
            ? goal.SyncStreakProgress((int)snapshot.CurrentValue)
            : goal.SyncStandardProgress((int)snapshot.CurrentValue);
        return new GoalProgressSyncOutcome(result.IsSuccess, result.IsSuccess && result.Value);
    }

    private async Task AwardCompletedGoalAsync(Guid userId, Guid goalId, CancellationToken cancellationToken)
    {
        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == goalId && g.UserId == userId,
            cancellationToken: cancellationToken);
        if (goal?.Status != GoalStatus.Completed)
            throw new InvalidOperationException($"Goal {goalId} was not persisted as completed before its rewards were processed.");

        await gamificationService.ProcessGoalCompleted(userId, cancellationToken);
    }
}
