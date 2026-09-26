namespace Orbit.Domain.Interfaces;

public sealed record HabitSummaryLogWindow(Guid HabitId, DateOnly From, DateOnly To);

public sealed record HabitSummaryLogFact(Guid HabitId, DateOnly Date, decimal Value);

public interface IHabitSummaryLogReader
{
    Task<IReadOnlyList<HabitSummaryLogFact>> ReadAsync(
        IReadOnlyCollection<HabitSummaryLogWindow> windows,
        CancellationToken cancellationToken = default);
}
