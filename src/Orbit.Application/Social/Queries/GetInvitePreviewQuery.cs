using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Queries;

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
