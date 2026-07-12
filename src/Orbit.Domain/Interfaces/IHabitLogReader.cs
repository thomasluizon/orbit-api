using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// DB-side read for a habit's recent logs that bounds the result set server-side, so the handler never
/// materializes a habit's full log history to order and cap it in memory. The lookback window, newest-first
/// ordering, and row cap all run inside the query, backed by the (HabitId, Date) index.
/// </summary>
public interface IHabitLogReader
{
    /// <summary>
    /// Loads a habit's logs dated on or after <paramref name="since"/>, ordered newest-first, capped at
    /// <paramref name="limit"/> rows. Ordering and the row cap run server-side in the query.
    /// </summary>
    Task<IReadOnlyList<HabitLog>> ReadRecentLogsAsync(
        Guid habitId,
        DateOnly since,
        int limit,
        CancellationToken cancellationToken = default);
}
