using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Services;

/// <summary>
/// The order of the app's habit list: depth first, and each level by <c>Position</c> (no position last), then
/// <c>CreatedAtUtc</c>, then <c>Id</c>, as <c>GetHabitScheduleQuery</c> and <c>HabitScheduleFilters</c> order roots and
/// children. A record whose parent is not in the set starts its own branch.
/// </summary>
public static class HabitAppListOrder
{
    public static IReadOnlyList<Habit> Order(IEnumerable<Habit> habits)
    {
        var all = habits.DistinctBy(habit => habit.Id).ToArray();
        var ids = all.Select(habit => habit.Id).ToHashSet();
        var children = all.Where(habit => habit.ParentHabitId is { } parent && ids.Contains(parent))
            .ToLookup(habit => habit.ParentHabitId!.Value);
        var ordered = new List<Habit>(all.Length);
        var visited = new HashSet<Guid>();
        foreach (var root in Level(all.Where(habit => habit.ParentHabitId is not { } parent || !ids.Contains(parent))))
            Visit(root);
        foreach (var rest in Level(all.Where(habit => !visited.Contains(habit.Id))))
            Visit(rest);
        return ordered;

        void Visit(Habit habit)
        {
            if (!visited.Add(habit.Id))
                return;
            ordered.Add(habit);
            foreach (var child in Level(children[habit.Id]))
                Visit(child);
        }
    }

    /// <summary>
    /// The records that share the title of <paramref name="record"/>, after normalizing case, accents and spacing, in
    /// the app list order. The place of a record in this group is its index plus one.
    /// </summary>
    public static IReadOnlyList<Habit> SameTitleGroup(Habit record, IEnumerable<Habit> habits, Func<string, string> titleKey)
    {
        var key = titleKey(record.Title);
        return [.. Order(habits).Where(habit => titleKey(habit.Title) == key)];
    }

    private static IEnumerable<Habit> Level(IEnumerable<Habit> habits) => habits
        .OrderBy(habit => habit.Position ?? int.MaxValue)
        .ThenBy(habit => habit.CreatedAtUtc)
        .ThenBy(habit => habit.Id);
}
