using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Application.Subscriptions.Commands;

public record CreatePortalSessionCommand(Guid UserId) : IRequest<Result<PortalResponse>>;

public class CreatePortalSessionCommandHandler(
    IGenericRepository<User> userRepository,
    IOptions<StripeSettings> stripeSettings,
    Stripe.BillingPortal.SessionService portalSessionService,
    ILogger<CreatePortalSessionCommandHandler> logger) : IRequestHandler<CreatePortalSessionCommand, Result<PortalResponse>>
{
    private readonly StripeSettings _settings = stripeSettings.Value;

    public async Task<Result<PortalResponse>> Handle(CreatePortalSessionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<PortalResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        if (string.IsNullOrEmpty(user.StripeCustomerId))
            return Result.Failure<PortalResponse>(ErrorMessages.SubscriptionNotFound, ErrorCodes.SubscriptionNotFound);

        try
        {
            var session = await portalSessionService.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = user.StripeCustomerId,
                ReturnUrl = _settings.SuccessUrl.Replace("?subscription=success", "")
            }, cancellationToken: cancellationToken);

            return Result.Success(new PortalResponse(session.Url));
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe API error during portal creation for user {UserId}", request.UserId);
            return Result.Failure<PortalResponse>("Payment service temporarily unavailable");
        }
    }
}
