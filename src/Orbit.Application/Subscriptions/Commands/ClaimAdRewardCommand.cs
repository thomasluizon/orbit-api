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
    IPayGateService payGate) : IRequestHandler<ClaimAdRewardCommand, Result<AdRewardResponse>>
{
    public async Task<Result<AdRewardResponse>> Handle(ClaimAdRewardCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);
        if (user is null)
            return Result.Failure<AdRewardResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var result = user.GrantAdReward();
        if (result.IsFailure)
            return Result.Failure<AdRewardResponse>(result.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var newLimit = await payGate.GetAiMessageLimit(request.UserId, cancellationToken);
        return Result.Success(new AdRewardResponse(5, user.AdRewardBonusMessages, newLimit));
    }
}
