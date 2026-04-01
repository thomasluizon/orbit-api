namespace Orbit.Domain.Interfaces;

/// <summary>
/// Provides efficient bulk-delete operations for resetting a user's account data.
/// </summary>
public interface IAccountResetRepository
{
    /// <summary>
    /// Deletes all user-created data for the given user in a single transaction.
    /// Does not modify the User entity itself.
    /// </summary>
    Task DeleteAllUserDataAsync(Guid userId, CancellationToken cancellationToken = default);
}
