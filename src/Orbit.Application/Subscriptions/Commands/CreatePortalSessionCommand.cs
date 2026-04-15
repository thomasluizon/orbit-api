using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Commands;

public record CreatePortalSessionCommand(Guid UserId) : IRequest<Result<PortalResponse>>;

public partial class CreatePortalSessionCommandHandler(
    IGenericRepository<User> userRepository,
    IOptions<StripeSettings> stripeSettings,
    IBillingService billingService,
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
            var returnUrl = _settings.SuccessUrl.Replace("?subscription=success", "");
            var url = await billingService.CreatePortalSessionAsync(user.StripeCustomerId, returnUrl, cancellationToken);
            return Result.Success(new PortalResponse(url));
        }
        catch (BillingProviderException ex)
        {
            LogStripePortalError(logger, ex, request.UserId);
            return Result.Failure<PortalResponse>("Payment service temporarily unavailable");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Billing provider error during portal creation for user {UserId}")]
    private static partial void LogStripePortalError(ILogger logger, Exception ex, Guid userId);
}
