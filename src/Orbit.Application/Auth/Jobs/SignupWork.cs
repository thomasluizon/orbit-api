using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Serializes signup jobs per account and refreshes completion checkpoints under the row lock.
/// Each independently retried effect saves its own checkpoint before releasing the lock.
/// </summary>
internal static class SignupWork
{
    public static Task RunAsync(Guid userId, IGenericRepository<User> users, IUnitOfWork work,
        Func<User, Task> operation) =>
        work.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await work.AcquireUserRowLockAsync(userId, cancellationToken);
            var user = await users.FindOneTrackedIgnoringFiltersAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
                return;

            await users.ReloadAsync(user, cancellationToken);
            await operation(user);
            await work.SaveChangesAsync(cancellationToken);
        }, CancellationToken.None);
}
