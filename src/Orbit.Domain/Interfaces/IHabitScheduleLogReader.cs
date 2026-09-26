namespace Orbit.Domain.Interfaces;

public sealed record HabitScheduleLogDay(
    Guid HabitId,
    DateOnly Date,
    int CompletedCount,
    int SkippedCount,
    bool HasLog);

public interface IHabitScheduleLogReader
{
    Task<IReadOnlyList<HabitScheduleLogDay>> ReadDaysAsync(
        IReadOnlyCollection<Guid> habitIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<Guid>> ReadResolvedDueDateIdsAsync(
        IReadOnlyCollection<Guid> habitIds,
        CancellationToken cancellationToken = default);
}
