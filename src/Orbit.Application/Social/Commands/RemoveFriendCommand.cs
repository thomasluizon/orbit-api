using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

/// <summary>
/// Removes the single friendship row between the caller and the target regardless of status, covering
/// unfriend, decline-incoming, and cancel-outgoing. Idempotent: a missing row is a successful no-op.
/// </summary>
public record RemoveFriendCommand(Guid UserId, Guid FriendUserId) : IRequest<Result>;

public class RemoveFriendCommandHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IGenericRepository<Friendship> friendshipRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<RemoveFriendCommand, Result>
{
    public async Task<Result> Handle(RemoveFriendCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();

        var friendship = await friendGraphService.FindFriendshipAsync(request.UserId, request.FriendUserId, cancellationToken);
        if (friendship is null)
            return Result.Success();

        friendshipRepository.Remove(friendship);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
