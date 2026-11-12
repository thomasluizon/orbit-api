using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat;

/// <summary>Shares immutable read inputs across the isolated scopes of one AI turn.</summary>
public sealed class ChatTurnReadScope : IDisposable
{
    private static readonly AsyncLocal<ChatTurnReadScope?> Ambient = new();
    private readonly ChatTurnReadScope? _previous;
    private readonly object _flagsGate = new();
    private Task<IReadOnlyList<string>>? _flagsTask;
    private readonly SemaphoreSlim _habitsGate = new(1, 1);

    public Guid UserId { get; }
    public User? User { get; }
    public IReadOnlySet<Guid> CompletedTodayIds { get; set; } = new HashSet<Guid>();
    public IReadOnlyList<Habit>? Habits { get; private set; }
    public IReadOnlyList<string>? EnabledFlags { get; set; }
    public IReadOnlyList<ResolvedFeatureFlag>? ResolvedFlags { get; set; }
    public static ChatTurnReadScope? For(Guid userId) => Ambient.Value is { } scope && scope.UserId == userId ? scope : null;

    public ChatTurnReadScope(Guid userId, User? user)
    {
        UserId = userId;
        User = user;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    public async Task<IReadOnlyList<Habit>> ReadHabitsAsync(Func<Task<IReadOnlyList<Habit>>> load, CancellationToken ct)
    {
        await _habitsGate.WaitAsync(ct);
        try { return Habits ??= await load(); }
        finally { _habitsGate.Release(); }
    }

    public Task<IReadOnlyList<Habit>> ReadHabitsAsync(IGenericRepository<Habit> repository, DateOnly today,
        bool includeChecklists, CancellationToken ct) => ReadHabitsAsync(async () =>
    {
        var candidates = await repository.ProjectAsync(habit => habit.UserId == UserId,
            q => ChatHabitCandidate.Select(q, today, includeChecklists), ct);
        CompletedTodayIds = candidates.Where(candidate => candidate.CompletedToday).Select(candidate => candidate.Schedule.Id).ToHashSet();
        return candidates.OrderBy(candidate => candidate.Position).ThenBy(candidate => candidate.Schedule.Id)
            .Select(candidate => candidate.ToHabit(UserId)).ToArray();
    }, ct);

    public Task<IReadOnlyList<string>> ReadEnabledFlagsAsync(Func<Task<IReadOnlyList<string>>> load)
    {
        lock (_flagsGate)
            return _flagsTask ??= load();
    }

    public void InvalidateHabits() => Habits = null;

    public void Dispose()
    {
        Ambient.Value = _previous;
        _habitsGate.Dispose();
    }
}
