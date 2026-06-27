using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Services;

/// <summary>
/// Loads the calling user and enforces the social opt-in gate shared by every social handler. On
/// success returns the tracked <see cref="User"/> so the caller can mutate it without re-loading; on
/// a disabled account returns SOCIAL_DISABLED (403). The two profile toggles bypass this guard so they
/// remain reachable while social is off.
/// </summary>
public class SocialAccessGuard(IGenericRepository<User> userRepository)
{
    public async Task<Result<User>> EnsureEnabledAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure<User>(ErrorMessages.UserNotFound);

        if (!user.SocialOptIn)
            return Result.Failure<User>(ErrorMessages.SocialDisabled);

        return Result.Success(user);
    }
}
