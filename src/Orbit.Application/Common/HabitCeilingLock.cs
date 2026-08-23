using Orbit.Domain.Interfaces;

namespace Orbit.Application.Common;

internal static class HabitCeilingLock
{
    public static string ForUser(Guid userId) => $"habit-ceiling:{userId}";

    public static Task AcquireAsync(
        IUnitOfWork unitOfWork,
        Guid userId,
        CancellationToken cancellationToken) =>
        unitOfWork.AcquireAdvisoryLockAsync(ForUser(userId), cancellationToken);

    public static Task<T> ExecuteAsync<T>(
        IUnitOfWork unitOfWork,
        Guid userId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            await AcquireAsync(unitOfWork, userId, transactionToken);
            return await operation(transactionToken);
        }, cancellationToken);
}
