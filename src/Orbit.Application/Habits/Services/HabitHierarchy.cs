using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Services;

internal static class HabitHierarchy
{
    public static bool WouldCreateCycle(Guid habitId, Guid targetParentId, IReadOnlyDictionary<Guid, Guid?> parentsById)
    {
        Guid? currentId = targetParentId;
        var visited = new HashSet<Guid>();
        while (currentId is not null && visited.Add(currentId.Value))
        {
            if (currentId == habitId)
                return true;
            currentId = parentsById.GetValueOrDefault(currentId.Value);
        }
        return false;
    }

    public static int GetDepth(Guid habitId, IReadOnlyDictionary<Guid, Guid?> parentsById)
    {
        var depth = 0;
        var currentId = parentsById.GetValueOrDefault(habitId);
        while (currentId is not null && parentsById.TryGetValue(currentId.Value, out var parentId))
        {
            depth++;
            currentId = parentId;
        }
        return depth;
    }

    public static int GetSubtreeHeight(Guid habitId, IReadOnlyDictionary<Guid, Guid?> parentsById)
    {
        var childrenByParent = parentsById
            .Where(pair => pair.Value is not null)
            .ToLookup(pair => pair.Value!.Value, pair => pair.Key);
        return MeasureHeight(habitId, childrenByParent);
    }

    private static int MeasureHeight(Guid habitId, ILookup<Guid, Guid> childrenByParent)
    {
        var children = childrenByParent[habitId].ToList();
        return children.Count == 0 ? 0 : 1 + children.Max(childId => MeasureHeight(childId, childrenByParent));
    }

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

    public static IReadOnlyList<Habit> SoftDeleteSubtree(
        Habit root,
        ILookup<Guid?, Habit> childrenByParentId,
        HashSet<Guid> deletedIds,
        DateTime deletedAtUtc)
    {
        var deleted = new List<Habit>();
        foreach (var habit in SelfAndDescendants(root, childrenByParentId))
        {
            if (!deletedIds.Add(habit.Id))
                continue;

            habit.RemoveAllGoals();
            habit.SoftDelete(deletedAtUtc);
            deleted.Add(habit);
        }

        return deleted;
    }
}
