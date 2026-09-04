using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Services;

public interface IGoalProgressReadSyncer
{
    Task<IReadOnlyDictionary<Guid, int>> ComputeFreshValuesAsync(Guid userId, DateOnly userToday, CancellationToken cancellationToken);
}

public class GoalProgressReadSyncer(
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService) : IGoalProgressReadSyncer
{
    public async Task<IReadOnlyDictionary<Guid, int>> ComputeFreshValuesAsync(
        Guid userId, DateOnly userToday, CancellationToken cancellationToken)
    {
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
        var candidates = await goalRepository.FindAsync(
            g => g.UserId == userId
                 && g.Status == GoalStatus.Active
                 && (g.Type == GoalType.Streak || g.Habits.Any()),
            cancellationToken);

        if (candidates.Count == 0)
            return new Dictionary<Guid, int>();

        var streakWindowStart = userToday.AddDays(-AppConstants.MaxStreakLookbackDays);
        var standardWindowStart = candidates
            .Where(g => g.Type == GoalType.Standard)
            .Select(g => g.CreatedAtUtc)
            .DefaultIfEmpty(DateTime.MaxValue)
            .Min();
        var goalIds = candidates.Select(g => g.Id).ToHashSet();

        var goals = await goalRepository.FindAsync(
            g => goalIds.Contains(g.Id),
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs.Where(l =>
                l.Date >= streakWindowStart || l.CreatedAtUtc >= standardWindowStart)),
            cancellationToken);

        var freshValues = new Dictionary<Guid, int>();
        foreach (var goal in goals)
        {
            var readValue = GoalProgressSyncService.ComputeReadValue(goal, userToday, weekStartDay);
            if (readValue.HasValue)
                freshValues[goal.Id] = readValue.Value;
        }

        return freshValues;
    }
}
