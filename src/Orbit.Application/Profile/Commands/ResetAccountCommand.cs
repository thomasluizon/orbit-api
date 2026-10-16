using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record ResetAccountCommand(Guid UserId) : IRequest<Result>, IConcurrencyRetryable;

public class ResetAccountCommandHandler(
    IGenericRepository<User> userRepository,
    IAccountResetRepository accountResetRepository,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<ResetAccountCommand, Result>
{
    public async Task<Result> Handle(ResetAccountCommand request, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync(ClosedMonthRecapLock.ForUser(request.UserId), ct);
            await unitOfWork.AcquireUserRowLockAsync(request.UserId, ct);

            var user = await userRepository.FindOneTrackedAsync(
                u => u.Id == request.UserId,
                cancellationToken: ct);

            if (user is null)
                return Result.Failure(ErrorMessages.UserNotFound);

            await accountResetRepository.DeleteAllUserDataAsync(request.UserId, ct);

            user.ResetAccount();
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);

        if (result.IsFailure)
            return result;

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }
}
