using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// DB-side reads for the social surface that must exclude blocked counterparties and bound their result
/// set, so the handlers never materialize a user's full friendship or cheer history to filter and cap it
/// in memory. Blocked exclusion is done as an anti-join against <see cref="BlockedUser"/> (either
/// direction) inside the query; both reads are ordered and capped server-side.
/// </summary>
public interface ISocialGraphReader
{
    /// <summary>
    /// Loads a user's friendship rows (both directions), excluding those whose other participant the user
    /// has blocked or been blocked by, ordered accepted-first then newest-first, capped at
    /// <paramref name="limit"/>. Deactivated counterparties stay the caller's concern (resolved via the
    /// deactivation-filtered <see cref="User"/> read), preserving the prior behavior.
    /// </summary>
    Task<IReadOnlyList<Friendship>> ReadVisibleFriendshipsAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a page of cheers for <paramref name="userId"/> in the given direction, created on or after
    /// <paramref name="since"/>, excluding blocked counterparties (either direction), newest-first, capped
    /// at <paramref name="limit"/>. <paramref name="isReceived"/> selects cheers the user received; false
    /// selects cheers the user sent.
    /// </summary>
    Task<IReadOnlyList<Cheer>> ReadCheersPageAsync(
        Guid userId,
        bool isReceived,
        DateTime since,
        int limit,
        CancellationToken cancellationToken = default);
}
