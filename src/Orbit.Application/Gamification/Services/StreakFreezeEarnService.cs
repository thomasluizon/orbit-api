using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Gamification.Services;

/// <summary>
/// Loads the tracked user and delegates to <see cref="User.TryEarnStreakFreezes"/>
/// to grant streak freezes based on the merit-based earning rules.
/// </summary>
public class StreakFreezeEarnService(
    IGenericRepository<User> userRepository) : IStreakFreezeEarnService
{
    public async Task<StreakFreezeEarnOutcome> EvaluateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);

        if (user is null)
            return new StreakFreezeEarnOutcome(0, 0, 0, 0);

        return user.TryEarnStreakFreezes();
    }
}
