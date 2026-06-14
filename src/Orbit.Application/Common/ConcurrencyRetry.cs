using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Common;

/// <summary>
/// Runs a load-mutate-save sequence on an optimistic-concurrency-tokened entity (xmin-mapped
/// <c>User</c>/<c>Goal</c>) so a lost-update race is corrected instead of overwriting a concurrent
/// writer. On <see cref="DbUpdateConcurrencyException"/> it discards pending changes, reloads the
/// entity to its current database state, and replays <paramref name="apply"/> once: re-evaluating
/// the guard against fresh values is what actually closes counter over-grants (e.g. the ad-reward
/// daily cap), and re-deriving any audit row (e.g. <c>GoalProgressLog</c>) from the reloaded value
/// keeps it coherent. A guard failure returns immediately and is never retried. A second
/// consecutive conflict returns <see cref="ErrorMessages.ConcurrentUpdateConflict"/> (mapped to
/// HTTP 409) rather than throwing.
/// </summary>
public static class ConcurrencyRetry
{
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
