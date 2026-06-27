using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public class FriendFeedReader(OrbitDbContext context) : IFriendFeedReader
{
    public async Task<IReadOnlyList<FriendFeedEvent>> ReadFeedPageAsync(
        IReadOnlyCollection<Guid> actorUserIds,
        DateTime? cursorCreatedAtUtc,
        Guid? cursorId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (actorUserIds.Count == 0)
            return [];

        var actorIds = actorUserIds as IReadOnlyList<Guid> ?? actorUserIds.ToList();

        var query = context.FriendFeedEvents
            .AsNoTracking()
            .Where(e => actorIds.Contains(e.ActorUserId));

        if (cursorCreatedAtUtc.HasValue && cursorId.HasValue)
        {
            var cursorTime = cursorCreatedAtUtc.Value;
            var cursorRowId = cursorId.Value;
            query = query.Where(e => EF.Functions.LessThan(
                ValueTuple.Create(e.CreatedAtUtc, e.Id),
                ValueTuple.Create(cursorTime, cursorRowId)));
        }

        return await query
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
