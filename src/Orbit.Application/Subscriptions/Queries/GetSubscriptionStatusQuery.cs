using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Queries;

public record GetSubscriptionStatusQuery(Guid UserId) : IRequest<Result<SubscriptionStatusResponse>>;

public class GetSubscriptionStatusQueryHandler(
    IGenericRepository<User> userRepository,
    IPayGateService payGate) : IRequestHandler<GetSubscriptionStatusQuery, Result<SubscriptionStatusResponse>>
{
    public async Task<Result<SubscriptionStatusResponse>> Handle(GetSubscriptionStatusQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<SubscriptionStatusResponse>(ErrorMessages.UserNotFound);

        return Result.Success(new SubscriptionStatusResponse(
            user.HasProAccess ? "pro" : "free",
            user.HasProAccess,
            user.IsTrialActive,
            user.TrialEndsAt,
            user.PlanExpiresAt,
            user.AiMessagesUsedThisMonth,
            await payGate.GetAiMessageLimit(user.Id, cancellationToken),
            user.IsLifetimePro,
            user.SubscriptionInterval?.ToString().ToLowerInvariant()));
    }
}
