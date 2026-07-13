using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Services;

/// <summary>
/// Reusable friend-graph reads shared by the social handlers: resolving a target user from a handle
/// (case-insensitive) or referral code, block detection in both directions, locating the single
/// friendship row between two users regardless of direction, and listing a user's accepted-friend ids.
/// Resolution returns null on a miss so callers can map it to a uniform not-found (no enumeration).
/// </summary>
public class FriendGraphService(
    IGenericRepository<User> userRepository,
    IGenericRepository<Friendship> friendshipRepository,
    IGenericRepository<BlockedUser> blockedUserRepository)
{
    public async Task<User?> ResolveTargetAsync(string? handle, string? referralCode, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(handle))
        {
            var normalized = handle.Trim().ToLowerInvariant();
            // EF Core cannot translate string.Equals(StringComparison) to SQL; the case-insensitive handle match stays as ToLower(). https://github.com/dotnet/efcore/issues/1222
#pragma warning disable CA1862
            var matches = await userRepository.FindAsync(
                u => u.Handle != null && u.Handle.ToLower() == normalized,
                cancellationToken);
#pragma warning restore CA1862
            return matches.Count > 0 ? matches[0] : null;
        }

        if (!string.IsNullOrWhiteSpace(referralCode))
        {
            var normalized = referralCode.Trim();
            var matches = await userRepository.FindAsync(
                u => u.ReferralCode == normalized,
                cancellationToken);
            return matches.Count > 0 ? matches[0] : null;
        }

        return null;
    }

    public async Task<bool> IsBlockedBetweenAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        return await blockedUserRepository.AnyAsync(
            x => (x.BlockerId == first && x.BlockedId == second)
                 || (x.BlockerId == second && x.BlockedId == first),
            cancellationToken);
    }

    public async Task<Friendship?> FindFriendshipAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        return await friendshipRepository.FindOneTrackedAsync(
            f => (f.RequesterId == first && f.AddresseeId == second)
                 || (f.RequesterId == second && f.AddresseeId == first),
            cancellationToken: cancellationToken);
    }

    public async Task<bool> AreAcceptedFriendsAsync(Guid first, Guid second, CancellationToken cancellationToken)
    {
        return await friendshipRepository.AnyAsync(
            f => f.Status == FriendshipStatus.Accepted
                 && ((f.RequesterId == first && f.AddresseeId == second)
                     || (f.RequesterId == second && f.AddresseeId == first)),
            cancellationToken);
    }

    public async Task<int> CountAcceptedFriendsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await friendshipRepository.CountAsync(
            f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId),
            cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetAcceptedFriendIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var friendships = await friendshipRepository.FindAsync(
            f => f.Status == FriendshipStatus.Accepted && (f.RequesterId == userId || f.AddresseeId == userId),
            cancellationToken);

        return friendships
            .Select(f => f.RequesterId == userId ? f.AddresseeId : f.RequesterId)
            .Distinct()
            .ToList();
    }
}
