namespace Orbit.Domain.Interfaces;

/// <summary>
/// Provides efficient bulk-delete operations for resetting a user's account data.
/// </summary>
public interface IAccountResetRepository
{
    /// <summary>
    /// Bulk-deletes all user-owned data for the given user. Issues many auto-committing
    /// <c>ExecuteDeleteAsync</c> calls and opens no transaction of its own, so callers MUST invoke it
    /// inside <see cref="IUnitOfWork.ExecuteInTransactionAsync"/> for the deletes to be atomic. Does not
    /// modify the User entity itself.
    /// </summary>
    Task DeleteAllUserDataAsync(Guid userId, CancellationToken cancellationToken = default);
}
