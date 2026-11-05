using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Services;

/// <summary>Shares the locked batch's tracked habits with derived logging and gamification.</summary>
internal sealed class HabitLogBatchReadScope : IDisposable
{
    private static readonly AsyncLocal<HabitLogBatchReadScope?> Current = new();
    private readonly HabitLogBatchReadScope? _previous = Current.Value;
    private readonly Guid _userId;
    internal Dictionary<Guid, Habit> Habits { get; }

    internal HabitLogBatchReadScope(Guid userId, Dictionary<Guid, Habit> habits)
    {
        _userId = userId;
        Habits = habits;
        Current.Value = this;
    }

    internal static HabitLogBatchReadScope? For(Guid userId) => Current.Value?._userId == userId ? Current.Value : null;

    public void Dispose() => Current.Value = _previous;
}
