using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Services;

public sealed record GoalStandardCompletionCount(Guid GoalId, int Count);

internal static class GoalStandardCompletionReader
{
    public static async Task<IReadOnlyDictionary<Guid, int>> ReadCountsAsync(
        IGenericRepository<Goal> goalRepository,
        Guid userId,
        IReadOnlyCollection<Guid> goalIds,
        CancellationToken cancellationToken)
    {
        if (goalIds.Count == 0)
            return new Dictionary<Guid, int>();

        var counts = await goalRepository.ProjectAsync(
            goal => goal.UserId == userId && goalIds.Contains(goal.Id) && goal.Type == GoalType.Standard,
            query => query.Select(goal => new GoalStandardCompletionCount(
                goal.Id,
                goal.Habits
                    .Where(habit => !habit.IsDeleted)
                    .SelectMany(habit => habit.Logs)
                    .Count(log => !log.IsDeleted && log.Value > 0 && log.CreatedAtUtc >= goal.CreatedAtUtc))),
            cancellationToken);

        return counts.ToDictionary(count => count.GoalId, count => count.Count);
    }
}
