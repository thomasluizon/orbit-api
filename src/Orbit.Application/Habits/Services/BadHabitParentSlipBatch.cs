using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

/// <summary>Loads missing ancestors and siblings once per tree level for a locked batch.</summary>
internal sealed class BadHabitParentSlipBatch(
    Dictionary<Guid, Habit> habits,
    IGenericRepository<Habit> repository,
    IReadOnlyCollection<DateOnly> dates)
{
    private bool _loaded;

    internal async Task<Habit?> ParentAsync(Habit child, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        return child.ParentHabitId is { } parentId && habits.TryGetValue(parentId, out var parent)
            && parent.UserId == child.UserId && parent.IsBadHabit && !parent.IsDeleted ? parent : null;
    }

    internal bool AllChildrenSlipped(Habit parent, DateOnly date)
    {
        var children = habits.Values.Where(habit => habit.ParentHabitId == parent.Id
            && habit.UserId == parent.UserId && habit.IsBadHabit && !habit.IsDeleted).ToArray();
        return children.Length > 0 && children.All(child => child.Logs.Any(log => log.Date == date
            && log.IsSlip && log.Value > 0 && !log.IsDeleted));
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded || dates.Count == 0) return;
        var from = new DateOnly(dates.Min().Year, 1, 1).AddDays(-6);
        var to = new DateOnly(dates.Max().Year, 12, 31).AddDays(6);
        var frontier = habits.Values.ToArray();
        var visited = new HashSet<Guid>();
        while (frontier.Length > 0)
        {
            var parentIds = frontier.Where(habit => habit.IsBadHabit && habit.ParentHabitId.HasValue)
                .Select(habit => habit.ParentHabitId!.Value).Where(visited.Add).ToHashSet();
            if (parentIds.Count == 0) break;
            var userId = frontier[0].UserId;
            var knownIds = habits.Keys.ToHashSet();
            var related = await repository.FindTrackedAsync(habit => habit.UserId == userId && habit.IsBadHabit
                && !habit.IsDeleted && !knownIds.Contains(habit.Id)
                && (parentIds.Contains(habit.Id) || habit.ParentHabitId.HasValue && parentIds.Contains(habit.ParentHabitId.Value)),
                query => query.Include(habit => habit.Logs.Where(log => log.Date >= from && log.Date <= to)),
                cancellationToken);
            foreach (var habit in related) habits.TryAdd(habit.Id, habit);
            frontier = parentIds.Where(habits.ContainsKey).Select(id => habits[id]).ToArray();
        }
        _loaded = true;
    }
}
