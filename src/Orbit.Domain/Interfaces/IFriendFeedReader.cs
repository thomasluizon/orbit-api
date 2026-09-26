using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public interface IFriendFeedReader
{
    Task<IReadOnlyList<FriendFeedEvent>> ReadFeedPageAsync(
        IReadOnlyCollection<Guid> actorUserIds,
        DateTime? cursorCreatedAtUtc,
        Guid? cursorId,
        int limit,
        CancellationToken cancellationToken = default);
}
