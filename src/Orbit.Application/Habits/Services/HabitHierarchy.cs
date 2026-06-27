using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Services;

internal static class HabitHierarchy
{
    /// <summary>
    /// Walks a habit and every descendant under it, depth-first, using a lookup of habits keyed by
    /// their <see cref="Habit.ParentHabitId"/>. Cascade soft-delete and restore share this so a parent
    /// operation reaches its whole sub-habit subtree.
    /// </summary>
    public static IEnumerable<Habit> SelfAndDescendants(Habit root, ILookup<Guid?, Habit> childrenByParentId)
    {
        yield return root;
        foreach (var child in childrenByParentId[root.Id])
            foreach (var descendant in SelfAndDescendants(child, childrenByParentId))
                yield return descendant;
    }
}
