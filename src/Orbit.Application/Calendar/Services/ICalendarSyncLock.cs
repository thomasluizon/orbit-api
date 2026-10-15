namespace Orbit.Application.Calendar.Services;

public interface ICalendarSyncLock
{
    /// <summary>
    /// Tries to reserve a user's calendar sync and locks their user row until the current
    /// transaction ends. Returns false immediately when another sync owns the reservation.
    /// </summary>
    Task<bool> TryAcquireAsync(Guid userId, CancellationToken cancellationToken);
}
