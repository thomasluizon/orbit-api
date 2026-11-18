using Orbit.Domain.Interfaces;

namespace Orbit.Application.Common;

/// <summary>
/// Runs an effect whose failure does not fail the command. Inside an open transaction the effect runs in a
/// savepoint, because PostgreSQL aborts the transaction after any failed statement and only a savepoint rollback
/// undoes that. The rollback clears the change tracker, so the command's tracked changes save first.
/// </summary>
public static class BestEffortEffect
{
    public static async Task<T?> RunAsync<T>(IUnitOfWork unitOfWork, Func<CancellationToken, Task<T>> effect,
        Action<Exception> onFailure, CancellationToken cancellationToken)
    {
        try
        {
            if (!unitOfWork.HasActiveTransaction)
                return await effect(cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await unitOfWork.ExecuteInSavepointAsync(effect, cancellationToken);
        }
        catch (Exception exception)
        {
            onFailure(exception);
            return default;
        }
    }

    public static Task RunAsync(IUnitOfWork unitOfWork, Func<CancellationToken, Task> effect,
        Action<Exception> onFailure, CancellationToken cancellationToken) =>
        RunAsync<bool>(unitOfWork, async ct =>
        {
            await effect(ct);
            return true;
        }, onFailure, cancellationToken);
}
