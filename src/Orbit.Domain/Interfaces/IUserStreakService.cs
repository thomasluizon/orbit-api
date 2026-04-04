using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IUserStreakService
{
    Task<UserStreakState?> RecalculateAsync(Guid userId, CancellationToken cancellationToken = default);
}
