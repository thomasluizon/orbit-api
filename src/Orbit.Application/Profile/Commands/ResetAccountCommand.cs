using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record ResetAccountCommand(Guid UserId) : IRequest<Result>;

public class ResetAccountCommandHandler(
    IGenericRepository<User> userRepository,
    IAccountResetRepository accountResetRepository,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<ResetAccountCommand, Result>
{
    public async Task<Result> Handle(ResetAccountCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            // Delete all user-created data (habits, goals, tags, etc.)
            await accountResetRepository.DeleteAllUserDataAsync(request.UserId, cancellationToken);

            // Reset user profile fields to defaults
            user.ResetAccount();
            await unitOfWork.SaveChangesAsync(cancellationToken);

            await unitOfWork.CommitTransactionAsync(cancellationToken);

            CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId);

            return Result.Success();
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            throw;
        }
    }
}
