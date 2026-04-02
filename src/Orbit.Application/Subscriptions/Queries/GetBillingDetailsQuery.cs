using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Application.Subscriptions.Queries;

public record GetBillingDetailsQuery(Guid UserId) : IRequest<Result<BillingDetailsResponse>>;

public class GetBillingDetailsQueryHandler(
    IGenericRepository<User> userRepository,
    SubscriptionService subscriptionService,
    InvoiceService invoiceService,
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
            var subOptions = new SubscriptionGetOptions();
            subOptions.AddExpand("default_payment_method");
            var subscription = await subscriptionService.GetAsync(user.StripeSubscriptionId, subOptions, cancellationToken: cancellationToken);

            PaymentMethodDto? paymentMethod = null;
            if (subscription.DefaultPaymentMethod?.Card is not null)
            {
                var card = subscription.DefaultPaymentMethod.Card;
                paymentMethod = new PaymentMethodDto(
                    card.Brand ?? "unknown",
                    card.Last4 ?? "****",
                    (int)card.ExpMonth,
                    (int)card.ExpYear);
            }

            var invoices = await invoiceService.ListAsync(new InvoiceListOptions
            {
                Customer = user.StripeCustomerId,
                Limit = 12
            }, cancellationToken: cancellationToken);

            var item = subscription.Items?.Data?.FirstOrDefault();
            var interval = GetSubscriptionInterval(subscription);

            return Result.Success(new BillingDetailsResponse(
                subscription.Status,
                item?.CurrentPeriodEnd ?? DateTime.UtcNow,
                subscription.CancelAtPeriodEnd,
                interval.ToString().ToLowerInvariant(),
                item?.Price?.UnitAmount ?? 0,
                subscription.Currency ?? "usd",
                paymentMethod,
                invoices.Data.Select(inv => new InvoiceDto(
                    inv.Id,
                    inv.Created,
                    inv.AmountPaid,
                    inv.Currency ?? "usd",
                    inv.Status ?? "unknown",
                    inv.HostedInvoiceUrl,
                    inv.InvoicePdf,
                    inv.BillingReason ?? "unknown"
                )).ToList()));
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Failed to fetch billing details from Stripe for user {UserId}", request.UserId);
            return Result.Failure<BillingDetailsResponse>("Failed to load billing details from payment provider");
        }
    }

    private static SubscriptionInterval GetSubscriptionInterval(Stripe.Subscription subscription)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        var interval = item?.Price?.Recurring?.Interval;

        return (interval, item?.Price?.Recurring?.IntervalCount ?? 1) switch
        {
            ("year", _) => SubscriptionInterval.Yearly,
            _ => SubscriptionInterval.Monthly
        };
    }
}
