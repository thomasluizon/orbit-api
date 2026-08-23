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
/// Owns the single persistence path for goal completion and its rewards. Derived candidates are
/// retained as identifiers rather than entity references because gamification concurrency retries
/// can reset the shared change tracker. Each identifier is resolved only when its progress and award
/// are processed, so a reset cannot detach a later goal that the operation still intends to save.
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
            await unitOfWork.SaveChangesAsync(transactionToken);
            await AwardCompletedGoalAsync(userId, goalId, transactionToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<GoalCompletionUpdate>> SyncDerivedGoalsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> goalIds,
        DateOnly userToday,
        bool passiveSync = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goalIds);

        var candidateIds = goalIds.Distinct().ToList();
        return unitOfWork.ExecuteInTransactionAsync<IReadOnlyList<GoalCompletionUpdate>>(async transactionToken =>
        {
            await unitOfWork.SaveChangesAsync(transactionToken);

            var updates = new List<GoalCompletionUpdate>();
            foreach (var goalId in candidateIds)
            {
                var goal = await goalRepository.FindOneTrackedAsync(
                    g => g.Id == goalId && g.UserId == userId,
                    query => query.Include(g => g.Habits).ThenInclude(h => h.Logs),
                    transactionToken);
                if (goal is null)
                    continue;

                var outcome = SyncProgress(goal, userToday, passiveSync);
                if (!outcome.Synced)
                    continue;

                var update = new GoalCompletionUpdate(
                    goal.Id,
                    goal.Title,
                    goal.CurrentValue,
                    goal.TargetValue,
                    outcome.JustCompleted);

                await unitOfWork.SaveChangesAsync(transactionToken);
                updates.Add(update);

                if (outcome.JustCompleted)
                    await AwardCompletedGoalAsync(userId, goalId, transactionToken);
            }

            return updates;
        }, cancellationToken);
    }

    private static GoalProgressSyncOutcome SyncProgress(Goal goal, DateOnly userToday, bool passiveSync)
    {
        if (passiveSync && goal.Type == GoalType.Streak)
        {
            var outcome = GoalStreakSyncService.SyncCurrentStreakIfNeeded(goal, userToday);
            return new GoalProgressSyncOutcome(outcome.Synced, outcome.JustCompleted);
        }

        return GoalProgressSyncService.SyncCurrentProgress(goal, userToday);
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
