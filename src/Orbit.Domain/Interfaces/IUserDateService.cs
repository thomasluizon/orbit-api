namespace Orbit.Domain.Interfaces;

public interface IUserDateService
{
    Task<DateOnly> GetUserTodayAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user's week-start preference (0 = Sunday, 1 = Monday) for anchoring
    /// week-based date math (flexible weekly windows, weekly retrospective period) so the
    /// server window agrees with the client's WeekStartDay-driven calendars and pickers.
    /// </summary>
    Task<int> GetUserWeekStartDayAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidate the cached timezone and week-start preference for a user. Must be called
    /// from any command that mutates User.TimeZone or User.WeekStartDay, otherwise subsequent
    /// date math can lag by up to 15 minutes.
    /// </summary>
    void InvalidateUserDatePreferences(Guid userId);
}
