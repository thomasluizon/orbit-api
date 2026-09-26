using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Common;

public static class ConcurrencyRetry
{
    private const int DefaultMaxAttempts = 3;

    public static async Task SaveWithRetryAsync(
        IUnitOfWork unitOfWork,
        Func<CancellationToken, Task> mutate,
        CancellationToken cancellationToken,
        int maxAttempts = DefaultMaxAttempts)
    {
        var attempt = 1;
        while (true)
        {
            if (attempt > 1)
                unitOfWork.ResetTracking();

            await mutate(cancellationToken);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                // Swallowed: the loop retries the save after resetting change tracking. https://github.com/thomasluizon/orbit-ui-mobile/issues/243
            }

            attempt++;
        }
    }

    public static async Task<Result<TEntity>> ExecuteAsync<TEntity>(
        IGenericRepository<TEntity> repository,
        IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<TEntity?>> load,
        Func<TEntity, Task<Result>> apply,
        AppError notFoundError,
        CancellationToken cancellationToken)
        where TEntity : Entity
    {
        var entity = await load(cancellationToken);
        if (entity is null)
            return Result.Failure<TEntity>(notFoundError);

        var guard = await apply(entity);
        if (guard.IsFailure)
            return guard.PropagateError<TEntity>();

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            unitOfWork.DiscardChanges();
            await repository.ReloadAsync(entity, cancellationToken);
        }

        var retryGuard = await apply(entity);
        if (retryGuard.IsFailure)
            return retryGuard.PropagateError<TEntity>();

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<TEntity>(ErrorMessages.ConcurrentUpdateConflict);
        }
    }
}
