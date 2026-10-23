using Hangfire;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

public sealed class RevokeAppleTokenOnDeletionJob(IAppleIdentityService apple, IGenericRepository<User> users, IUnitOfWork unitOfWork)
{
    [AutomaticRetry(Attempts = 10)]
    public Task ExecuteAsync(Guid userId, CancellationToken cancellationToken) => unitOfWork.ExecuteOnceInTransactionAsync(async ct =>
    {
        await unitOfWork.AcquireUserRowLockAsync(userId, ct);
        var user = await users.FindOneTrackedIgnoringFiltersAsync(u => u.Id == userId, ct);
        if (user is not { IsDeactivated: true, AppleRefreshToken: not null, AppleTokenClientId: not null })
            return;
        var token = user.AppleRefreshToken;
        await apple.RevokeAsync(token, user.AppleTokenClientId, ct);
        user.ClearRevokedAppleToken(token);
        await unitOfWork.SaveChangesAsync(ct);
    }, cancellationToken);
}
