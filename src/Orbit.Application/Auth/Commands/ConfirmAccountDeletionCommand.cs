using Hangfire;
using MediatR;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record ConfirmAccountDeletionCommand(Guid UserId, string Code) : IRequest<Result<DateTime>>;

public class ConfirmAccountDeletionCommandHandler(
    EmailChallengeService challengeService,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IBackgroundJobClient backgroundJobClient,
    IAuthSessionService authSessionService,
    IGenericRepository<ApiKey> apiKeyRepository) : IRequestHandler<ConfirmAccountDeletionCommand, Result<DateTime>>
{
    public async Task<Result<DateTime>> Handle(ConfirmAccountDeletionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<DateTime>(ErrorMessages.UserNotFound);

        var confirmation = await challengeService.ConfirmAsync(
            EmailChallengeOperation.AccountDeletion,
            user.Email,
            request.Code,
            cancellationToken);
        if (confirmation.IsFailure)
            return confirmation.PropagateError<DateTime>();

        var result = await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await unitOfWork.AcquireUserRowLockAsync(request.UserId, token);
            var revocation = await authSessionService.RevokeAllSessionsAsync(request.UserId, token);
            if (revocation.IsFailure)
                return revocation.PropagateError<DateTime>();
            user = await userRepository.GetByIdAsync(request.UserId, token);
            if (user is null)
                return Result.Failure<DateTime>(ErrorMessages.UserNotFound);
            await userRepository.ReloadAsync(user, token);
            var nowAtUtc = DateTime.UtcNow;
            var scheduledDate = user.HasProAccess && user.PlanExpiresAt.HasValue && user.PlanExpiresAt.Value > nowAtUtc
                ? user.PlanExpiresAt.Value.AddDays(7) : nowAtUtc.AddDays(7);
            var maximumScheduledDate = nowAtUtc.AddDays(AppConstants.MaxDeletionGraceDays);
            scheduledDate = scheduledDate < maximumScheduledDate ? scheduledDate : maximumScheduledDate;
            user.Deactivate(scheduledDate);
            foreach (var key in await apiKeyRepository.FindTrackedAsync(k => k.UserId == user.Id && !k.IsRevoked, token))
                key.Revoke();
            await unitOfWork.SaveChangesAsync(token);
            return Result.Success(scheduledDate);
        }, cancellationToken);
        if (result.IsFailure)
            return result;
        if (user is null)
            throw new InvalidOperationException("A successful deletion requires the deactivated account.");
        CancelSubscriptionOnDeletionJob.EnqueueCancellation(backgroundJobClient, user);
        if (user.AppleRefreshToken is not null)
            backgroundJobClient.Enqueue<RevokeAppleTokenOnDeletionJob>(job => job.ExecuteAsync(user.Id, CancellationToken.None));

        return result;
    }
}
