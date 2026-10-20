using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// DB-side reads for habit logs. Date bounds and newest-first ordering run server-side,
/// backed by the (HabitId, Date) index. Recent reads also apply a row cap.
/// </summary>
public interface IHabitLogReader
{
    Task<DateOnly?> GetLastCompletionDateAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a habit's logs dated on or after <paramref name="since"/>, ordered newest-first, capped at
    /// <paramref name="limit"/> rows. Ordering and the row cap run server-side in the query.
    /// </summary>
    Task<IReadOnlyList<HabitLog>> ReadRecentLogsAsync(
        Guid habitId,
        DateOnly since,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Loads every log in the inclusive date range, ordered newest-first.</summary>
    Task<IReadOnlyList<HabitLog>> ReadRangeLogsAsync(
        Guid habitId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}
