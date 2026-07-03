using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Queries;

/// <summary>
/// Resolves a referral code to its owner so a signed-in user opening an invite link can see who
/// invited them before sending a friend request. Mirrors the friend-request guards: the caller must
/// have social enabled, and an unknown/malformed code, a private (opted-out) owner, or a block in
/// either direction all map to a uniform not-found so the endpoint never enumerates accounts.
/// <see cref="InvitePreviewView.IsSelf"/> marks the caller's own code; IsAlreadyFriend / HasPendingRequest
/// read the single friendship row between the two users, in either direction.
/// </summary>
public record InvitePreviewView(
    string Handle,
    string DisplayName,
    bool IsSelf,
    bool IsAlreadyFriend,
    bool HasPendingRequest);

public record GetInvitePreviewQuery(Guid UserId, string ReferralCode) : IRequest<Result<InvitePreviewView>>;

public class GetInvitePreviewQueryHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService) : IRequestHandler<GetInvitePreviewQuery, Result<InvitePreviewView>>
{
    private const string ReferralCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int ReferralCodeLength = 8;

    public async Task<Result<InvitePreviewView>> Handle(GetInvitePreviewQuery request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<InvitePreviewView>();

        if (!IsWellFormedReferralCode(request.ReferralCode))
            return Result.Failure<InvitePreviewView>(ErrorMessages.UserNotFound);

        var owner = await friendGraphService.ResolveTargetAsync(null, request.ReferralCode, cancellationToken);
        if (owner is null)
            return Result.Failure<InvitePreviewView>(ErrorMessages.UserNotFound);

        var isSelf = owner.Id == request.UserId;

        if (!isSelf && !owner.SocialOptIn)
            return Result.Failure<InvitePreviewView>(ErrorMessages.UserNotFound);

        if (!isSelf && await friendGraphService.IsBlockedBetweenAsync(request.UserId, owner.Id, cancellationToken))
            return Result.Failure<InvitePreviewView>(ErrorMessages.UserNotFound);

        var friendship = isSelf
            ? null
            : await friendGraphService.FindFriendshipAsync(request.UserId, owner.Id, cancellationToken);

        return Result.Success(new InvitePreviewView(
            owner.Handle ?? string.Empty,
            owner.Name,
            isSelf,
            friendship?.Status == FriendshipStatus.Accepted,
            friendship?.Status == FriendshipStatus.Pending));
    }

    private static bool IsWellFormedReferralCode(string code) =>
        code.Length == ReferralCodeLength && code.All(ReferralCodeAlphabet.Contains);
}
