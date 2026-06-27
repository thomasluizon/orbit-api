using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// Keyset-paginated read over <see cref="FriendFeedEvent"/> for a fixed set of actor ids, ordered
/// newest-first by (CreatedAtUtc, Id). The cursor is the last row of the previous page; passing null
/// reads the first page. Returns at most <paramref name="limit"/> rows (callers fetch one extra to
/// detect "has next"). Set-based and friend-count-independent — the actor join for display fields is
/// done by the caller against its already-loaded friend set, so there is no N+1.
/// </summary>
public interface IFriendFeedReader
{
    Task<IReadOnlyList<FriendFeedEvent>> ReadFeedPageAsync(
        IReadOnlyCollection<Guid> actorUserIds,
        DateTime? cursorCreatedAtUtc,
        Guid? cursorId,
        int limit,
        CancellationToken cancellationToken = default);
}
