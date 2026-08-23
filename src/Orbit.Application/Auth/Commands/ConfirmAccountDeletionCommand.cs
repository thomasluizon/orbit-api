using MediatR;
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
    IUnitOfWork unitOfWork) : IRequestHandler<ConfirmAccountDeletionCommand, Result<DateTime>>
{
    public async Task<Result<DateTime>> Handle(ConfirmAccountDeletionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<DateTime>(ErrorMessages.UserNotFound);

        var confirmation = challengeService.Confirm(
            EmailChallengeOperation.AccountDeletion,
            user.Email,
            request.Code);
        if (confirmation.IsFailure)
            return confirmation.PropagateError<DateTime>();

        var nowAtUtc = DateTime.UtcNow;
        var scheduledDate = user.HasProAccess && user.PlanExpiresAt.HasValue && user.PlanExpiresAt.Value > nowAtUtc
            ? user.PlanExpiresAt.Value.AddDays(7)
            : nowAtUtc.AddDays(7);
        var maximumScheduledDate = nowAtUtc.AddDays(AppConstants.MaxDeletionGraceDays);
        scheduledDate = scheduledDate < maximumScheduledDate ? scheduledDate : maximumScheduledDate;

        user.Deactivate(scheduledDate);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(scheduledDate);
    }
}
