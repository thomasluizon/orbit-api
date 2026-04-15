using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Commands;

public record ClaimAdRewardCommand(Guid UserId) : IRequest<Result<AdRewardResponse>>;

public class ClaimAdRewardCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IPayGateService payGate) : IRequestHandler<ClaimAdRewardCommand, Result<AdRewardResponse>>
{
    public async Task<Result<AdRewardResponse>> Handle(ClaimAdRewardCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);
        if (user is null)
            return Result.Failure<AdRewardResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var userToday = await userDateService.GetUserTodayAsync(user.Id, cancellationToken);
        var result = user.GrantAdReward(userToday);
        if (result.IsFailure)
            return Result.Failure<AdRewardResponse>(result.Error);

        // Optimistic concurrency token on User (RowVersion) backstops two simultaneous
        // claims at the same daily count: one save will succeed, the other will throw
        // DbUpdateConcurrencyException and surface as a retryable error.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var newLimit = await payGate.GetAiMessageLimit(request.UserId, cancellationToken);
        return Result.Success(new AdRewardResponse(5, user.AdRewardBonusMessages, newLimit));
    }
}
