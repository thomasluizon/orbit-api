using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Services;

/// <summary>
/// Computes a user's active Streak goals' live current values from their linked habits' logs so a
/// read can surface the fresh streak instead of the last-synced one. It reads without tracking and
/// never persists or completes anything: the returned map is projected into the response, leaving
/// Active to Completed completion and its gamification to the write paths and the hosted sweep.
/// </summary>
public interface IStreakGoalReadSyncer
{
    Task<IReadOnlyDictionary<Guid, int>> ComputeFreshValuesAsync(Guid userId, DateOnly userToday, CancellationToken cancellationToken);
}

public class StreakGoalReadSyncer(IGenericRepository<Goal> goalRepository) : IStreakGoalReadSyncer
{
    public async Task<IReadOnlyDictionary<Guid, int>> ComputeFreshValuesAsync(
        Guid userId, DateOnly userToday, CancellationToken cancellationToken)
    {
        var activeStreakGoals = await goalRepository.FindAsync(
            g => g.UserId == userId && g.Type == GoalType.Streak && g.Status == GoalStatus.Active,
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken);

        var freshValues = new Dictionary<Guid, int>();
        foreach (var goal in activeStreakGoals)
        {
            var readValue = GoalStreakSyncService.ComputeReadValue(goal, userToday);
            if (readValue.HasValue)
                freshValues[goal.Id] = readValue.Value;
        }

        return freshValues;
    }
}
