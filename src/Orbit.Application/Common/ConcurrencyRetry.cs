using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Common;

public static class ConcurrencyRetry
{
    public const int DefaultMaxAttempts = 3;

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task> reset,
        CancellationToken cancellationToken,
        Func<Exception, bool>? shouldRetry = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (attempt < maxAttempts
                && (shouldRetry?.Invoke(exception) ?? exception is DbUpdateConcurrencyException))
            {
                await reset(cancellationToken);
            }
        }
    }

    public static Task<T> RunAsync<T>(
        IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        Func<Exception, bool>? shouldRetry = null) =>
        RunAsync(operation, _ =>
        {
            unitOfWork.ResetTracking();
            return Task.CompletedTask;
        }, cancellationToken, shouldRetry);

    public static async Task SaveWithRetryAsync(
        IUnitOfWork unitOfWork,
        Func<CancellationToken, Task> mutate,
        CancellationToken cancellationToken,
        int maxAttempts = DefaultMaxAttempts)
    {
        await RunAsync(async ct =>
        {
            await mutate(ct);
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, _ =>
        {
            unitOfWork.ResetTracking();
            return Task.CompletedTask;
        }, cancellationToken, maxAttempts: maxAttempts);
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
