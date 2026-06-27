using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

/// <summary>
/// Blocks a user. Blocking is idempotent and also tears down any existing friendship between the two
/// (a block must immediately stop feed visibility, pushes, and future requests in both directions).
/// </summary>
public record BlockUserCommand(Guid UserId, Guid BlockedUserId) : IRequest<Result>;

public class BlockUserCommandHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IGenericRepository<User> userRepository,
    IGenericRepository<BlockedUser> blockedUserRepository,
    IGenericRepository<Friendship> friendshipRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<BlockUserCommand, Result>
{
    public async Task<Result> Handle(BlockUserCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();

        var alreadyBlocked = await blockedUserRepository.AnyAsync(
            b => b.BlockerId == request.UserId && b.BlockedId == request.BlockedUserId,
            cancellationToken);
        if (alreadyBlocked)
            return Result.Success();

        var targetExists = await userRepository.AnyAsync(u => u.Id == request.BlockedUserId, cancellationToken);
        if (!targetExists)
            return Result.Failure(ErrorMessages.UserNotFound);

        var createResult = BlockedUser.Create(request.UserId, request.BlockedUserId);
        if (createResult.IsFailure)
            return createResult;

        await blockedUserRepository.AddAsync(createResult.Value, cancellationToken);

        var friendship = await friendGraphService.FindFriendshipAsync(request.UserId, request.BlockedUserId, cancellationToken);
        if (friendship is not null)
            friendshipRepository.Remove(friendship);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
