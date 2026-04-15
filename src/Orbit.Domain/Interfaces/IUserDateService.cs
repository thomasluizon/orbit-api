namespace Orbit.Domain.Interfaces;

public interface IUserDateService
{
    Task<DateOnly> GetUserTodayAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidate the cached timezone for a user. Must be called from any command that
    /// mutates User.TimeZone, otherwise subsequent date math can lag by up to 15 minutes.
    /// </summary>
    void InvalidateUserTimezone(Guid userId);
}
