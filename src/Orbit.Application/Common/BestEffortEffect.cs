using Orbit.Domain.Interfaces;

namespace Orbit.Application.Common;

/// <summary>
/// Runs an effect whose failure does not fail the command. Inside an open transaction the effect runs in a
/// savepoint, because PostgreSQL aborts the transaction after any failed statement and only a savepoint rollback
/// undoes that. The rollback clears the change tracker, so the command's tracked changes save first. That save
/// belongs to the command: its failure fails the command and is not reported as a failure of the effect.
/// </summary>
public static class BestEffortEffect
{
    public static async Task<T?> RunAsync<T>(IUnitOfWork unitOfWork, Func<CancellationToken, Task<T>> effect,
        Action<Exception> onFailure, CancellationToken cancellationToken)
    {
        var inTransaction = unitOfWork.HasActiveTransaction;
        if (inTransaction)
            await unitOfWork.SaveChangesAsync(cancellationToken);
        try
        {
            return inTransaction
                ? await unitOfWork.ExecuteInSavepointAsync(effect, cancellationToken)
                : await effect(cancellationToken);
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
