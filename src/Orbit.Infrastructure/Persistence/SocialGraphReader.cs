using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public class SocialGraphReader(OrbitDbContext context) : ISocialGraphReader
{
    public async Task<IReadOnlyList<Friendship>> ReadVisibleFriendshipsAsync(
        Guid userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await BuildVisibleFriendships(context.Friendships.AsNoTracking(), context.BlockedUsers, userId, limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Cheer>> ReadCheersPageAsync(
        Guid userId,
        bool isReceived,
        DateTime since,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await BuildVisibleCheers(context.Cheers.AsNoTracking(), context.BlockedUsers, userId, isReceived, since, limit)
            .ToListAsync(cancellationToken);
    }

    internal static IQueryable<Friendship> BuildVisibleFriendships(
        IQueryable<Friendship> friendships,
        IQueryable<BlockedUser> blockedUsers,
        Guid userId,
        int limit)
    {
        return friendships
            .Where(f => f.RequesterId == userId || f.AddresseeId == userId)
            .Where(f => !blockedUsers.Any(b =>
                (b.BlockerId == userId && (b.BlockedId == f.RequesterId || b.BlockedId == f.AddresseeId))
                || (b.BlockedId == userId && (b.BlockerId == f.RequesterId || b.BlockerId == f.AddresseeId))))
            .OrderByDescending(f => f.Status == FriendshipStatus.Accepted)
            .ThenByDescending(f => f.CreatedAtUtc)
            .Take(limit);
    }

    internal static IQueryable<Cheer> BuildVisibleCheers(
        IQueryable<Cheer> cheers,
        IQueryable<BlockedUser> blockedUsers,
        Guid userId,
        bool isReceived,
        DateTime since,
        int limit)
    {
        var withinWindow = cheers.Where(c => c.CreatedAtUtc >= since);

        var directed = isReceived
            ? withinWindow.Where(c => c.RecipientId == userId
                && !blockedUsers.Any(b =>
                    (b.BlockerId == userId && b.BlockedId == c.SenderId)
                    || (b.BlockedId == userId && b.BlockerId == c.SenderId)))
            : withinWindow.Where(c => c.SenderId == userId
                && !blockedUsers.Any(b =>
                    (b.BlockerId == userId && b.BlockedId == c.RecipientId)
                    || (b.BlockedId == userId && b.BlockerId == c.RecipientId)));

        return directed
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(limit);
    }
}
