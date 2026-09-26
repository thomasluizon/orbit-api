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
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var saved = await ConcurrencyRetry.ExecuteAsync(
            userRepository,
            unitOfWork,
            ct => userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: ct),
            user => Task.FromResult(user.GrantAdReward(userToday)),
            ErrorMessages.UserNotFound,
            cancellationToken);

        if (saved.IsFailure)
            return saved.PropagateError<AdRewardResponse>();

        var newLimit = await payGate.GetAiMessageLimit(request.UserId, cancellationToken);
        return Result.Success(new AdRewardResponse(5, saved.Value.AdRewardBonusMessages, newLimit));
    }
}
