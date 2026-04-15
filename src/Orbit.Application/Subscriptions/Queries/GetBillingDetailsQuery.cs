using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Queries;

public record GetBillingDetailsQuery(Guid UserId) : IRequest<Result<BillingDetailsResponse>>;

public partial class GetBillingDetailsQueryHandler(
    IGenericRepository<User> userRepository,
    IBillingService billingService,
    ILogger<GetBillingDetailsQueryHandler> logger) : IRequestHandler<GetBillingDetailsQuery, Result<BillingDetailsResponse>>
{
    public async Task<Result<BillingDetailsResponse>> Handle(GetBillingDetailsQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<BillingDetailsResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        if (string.IsNullOrEmpty(user.StripeSubscriptionId) || string.IsNullOrEmpty(user.StripeCustomerId))
            return Result.Failure<BillingDetailsResponse>("No active subscription found");

        try
        {
            var subscription = await billingService.GetSubscriptionDetailsAsync(user.StripeSubscriptionId, cancellationToken);
            if (subscription is null)
                return Result.Failure<BillingDetailsResponse>("Subscription not found");

            var invoices = await billingService.ListInvoicesAsync(user.StripeCustomerId, limit: 12, cancellationToken);

            var paymentMethod = subscription.PaymentMethod is null
                ? null
                : new PaymentMethodDto(
                    subscription.PaymentMethod.Brand,
                    subscription.PaymentMethod.Last4,
                    subscription.PaymentMethod.ExpMonth,
                    subscription.PaymentMethod.ExpYear);

            return Result.Success(new BillingDetailsResponse(
                subscription.Status,
                subscription.CurrentPeriodEnd,
                subscription.CancelAtPeriodEnd,
                subscription.Interval.ToString().ToLowerInvariant(),
                subscription.UnitAmount,
                subscription.Currency,
                paymentMethod,
                invoices.Select(inv => new InvoiceDto(
                    inv.Id,
                    inv.Created,
                    inv.AmountPaid,
                    inv.Currency,
                    inv.Status,
                    inv.HostedUrl,
                    inv.PdfUrl,
                    inv.BillingReason)).ToList()));
        }
        catch (BillingProviderException ex)
        {
            LogFetchBillingDetailsFailed(logger, ex, request.UserId);
            return Result.Failure<BillingDetailsResponse>("Failed to load billing details from payment provider");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to fetch billing details from billing provider for user {UserId}")]
    private static partial void LogFetchBillingDetailsFailed(ILogger logger, Exception ex, Guid userId);
}
