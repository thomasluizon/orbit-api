namespace Orbit.Domain.Interfaces;

public interface IUserDateService
{
    Task<DateOnly> GetUserTodayAsync(Guid userId, CancellationToken cancellationToken = default);
}
