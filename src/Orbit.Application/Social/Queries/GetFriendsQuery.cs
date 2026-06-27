using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Queries;

public record FriendSummary(Guid UserId, string Handle, string DisplayName, int CurrentStreak);

public record FriendRequestSummary(Guid Id, Guid UserId, string Handle, string DisplayName, DateTime CreatedAtUtc);

public record FriendsResponse(
    IReadOnlyList<FriendSummary> Friends,
    IReadOnlyList<FriendRequestSummary> IncomingRequests,
    IReadOnlyList<FriendRequestSummary> OutgoingRequests);

public record GetFriendsQuery(Guid UserId) : IRequest<Result<FriendsResponse>>;

public class GetFriendsQueryHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<Friendship> friendshipRepository,
    IGenericRepository<BlockedUser> blockedUserRepository,
    IGenericRepository<User> userRepository) : IRequestHandler<GetFriendsQuery, Result<FriendsResponse>>
{
    public async Task<Result<FriendsResponse>> Handle(GetFriendsQuery request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<FriendsResponse>();

        var friendships = await friendshipRepository.FindAsync(
            f => f.RequesterId == request.UserId || f.AddresseeId == request.UserId,
            cancellationToken);

        var blocks = await blockedUserRepository.FindAsync(
            b => b.BlockerId == request.UserId || b.BlockedId == request.UserId,
            cancellationToken);
        var blockedIds = blocks
            .Select(b => b.BlockerId == request.UserId ? b.BlockedId : b.BlockerId)
            .ToHashSet();

        var visible = friendships
            .Where(f => !blockedIds.Contains(OtherId(f, request.UserId)))
            .ToList();

        var otherIds = visible.Select(f => OtherId(f, request.UserId)).ToHashSet();
        var users = await userRepository.FindAsync(u => otherIds.Contains(u.Id), cancellationToken);
        var usersById = users.ToDictionary(u => u.Id);

        var friends = new List<FriendSummary>();
        var incoming = new List<FriendRequestSummary>();
        var outgoing = new List<FriendRequestSummary>();

        foreach (var friendship in visible)
        {
            var otherId = OtherId(friendship, request.UserId);
            if (!usersById.TryGetValue(otherId, out var other))
                continue;

            if (friendship.Status == FriendshipStatus.Accepted)
            {
                friends.Add(new FriendSummary(otherId, other.Handle ?? string.Empty, other.Name, other.CurrentStreak));
            }
            else if (friendship.AddresseeId == request.UserId)
            {
                incoming.Add(new FriendRequestSummary(friendship.Id, otherId, other.Handle ?? string.Empty, other.Name, friendship.CreatedAtUtc));
            }
            else
            {
                outgoing.Add(new FriendRequestSummary(friendship.Id, otherId, other.Handle ?? string.Empty, other.Name, friendship.CreatedAtUtc));
            }
        }

        var response = new FriendsResponse(
            friends.OrderBy(f => f.DisplayName).ToList(),
            incoming.OrderByDescending(r => r.CreatedAtUtc).ToList(),
            outgoing.OrderByDescending(r => r.CreatedAtUtc).ToList());

        return Result.Success(response);
    }

    private static Guid OtherId(Friendship friendship, Guid userId) =>
        friendship.RequesterId == userId ? friendship.AddresseeId : friendship.RequesterId;
}
