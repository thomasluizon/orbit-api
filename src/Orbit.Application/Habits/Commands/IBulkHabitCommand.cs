namespace Orbit.Application.Habits.Commands;

/// <summary>
/// A bulk habit command carrying the owning user and a list of per-habit items.
/// Lets the shared validation base treat bulk log/skip commands uniformly.
/// </summary>
public interface IBulkHabitCommand<out TItem>
    where TItem : IBulkHabitItem
{
    Guid UserId { get; }
    IReadOnlyList<TItem> Items { get; }
}

/// <summary>A single item within a bulk habit command, identified by its habit id.</summary>
public interface IBulkHabitItem
{
    Guid HabitId { get; }
}
