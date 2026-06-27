using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

/// <summary>
/// Lifts a block. Idempotent. Does not restore the prior friendship — the users must reconnect.
/// </summary>
public record UnblockUserCommand(Guid UserId, Guid BlockedUserId) : IRequest<Result>;

public class UnblockUserCommandHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<BlockedUser> blockedUserRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UnblockUserCommand, Result>
{
    public async Task<Result> Handle(UnblockUserCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();

        var block = await blockedUserRepository.FindOneTrackedAsync(
            b => b.BlockerId == request.UserId && b.BlockedId == request.BlockedUserId,
            cancellationToken: cancellationToken);

        if (block is null)
            return Result.Success();

        blockedUserRepository.Remove(block);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
