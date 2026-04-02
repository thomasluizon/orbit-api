using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Stripe;
using Stripe.Checkout;

namespace Orbit.Application.Subscriptions.Commands;

public record HandleWebhookCommand(string Json, string Signature) : IRequest<Result>;

public class HandleWebhookCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IOptions<StripeSettings> stripeSettings,
    SubscriptionService subscriptionService,
    ILogger<HandleWebhookCommandHandler> logger) : IRequestHandler<HandleWebhookCommand, Result>
{
    private readonly StripeSettings _settings = stripeSettings.Value;

    public async Task<Result> Handle(HandleWebhookCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Stripe webhook received, body length: {Length}", request.Json.Length);

        if (string.IsNullOrEmpty(_settings.WebhookSecret))
        {
            logger.LogCritical("Stripe WebhookSecret is not configured -- rejecting webhook");
            return Result.Failure("Webhook secret not configured");
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                request.Json,
                request.Signature,
                _settings.WebhookSecret,
                throwOnApiVersionMismatch: true);
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe webhook signature verification failed");
            return Result.Failure("Invalid webhook signature");
        }

        logger.LogInformation("Stripe event type: {Type}, id: {Id}", stripeEvent.Type, stripeEvent.Id);

        try
        {
            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                    await HandleCheckoutSessionCompleted(stripeEvent, cancellationToken);
                    break;

                case "invoice.paid":
                    await HandleInvoicePaid(stripeEvent, cancellationToken);
                    break;

                case "customer.subscription.deleted":
                    await HandleSubscriptionDeleted(stripeEvent, cancellationToken);
                    break;

                case "customer.subscription.updated":
                    await HandleSubscriptionUpdated(stripeEvent, cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing Stripe webhook event {Type}", stripeEvent.Type);
            return Result.Failure("Error processing webhook event");
        }

        return Result.Success();
    }

    private async Task HandleCheckoutSessionCompleted(Event stripeEvent, CancellationToken ct)
    {
        var session = stripeEvent.Data.Object as Session;
        logger.LogInformation("Checkout session: id={Id}, subscription={SubId}, metadata keys={Keys}",
            session?.Id, session?.SubscriptionId ?? session?.Subscription?.Id,
            session?.Metadata != null ? string.Join(",", session.Metadata.Keys) : "null");

        var subscriptionId = session?.SubscriptionId ?? session?.Subscription?.Id;

        if (session?.Metadata?.TryGetValue("userId", out var userIdStr) == true
            && Guid.TryParse(userIdStr, out var uid))
        {
            var user = await userRepository.FindOneTrackedAsync(u => u.Id == uid, cancellationToken: ct);
            logger.LogInformation("User found: {Found}, subscriptionId: {SubId}", user is not null, subscriptionId);

            if (user is not null && subscriptionId is not null)
            {
                var subscription = await subscriptionService.GetAsync(subscriptionId, cancellationToken: ct);
                var periodEnd = subscription.Items?.Data?.Count > 0
                    ? subscription.Items.Data[0].CurrentPeriodEnd
                    : DateTime.UtcNow.AddMonths(1);

                user.SetStripeCustomerId(session.CustomerId ?? session.Customer?.Id ?? "");
                user.SetStripeSubscription(subscriptionId, periodEnd, GetSubscriptionInterval(subscription));

                if (!string.IsNullOrEmpty(user.ReferralCouponId))
                {
                    logger.LogInformation("Clearing referral coupon {CouponId} for user {UserId} after checkout", user.ReferralCouponId, uid);
                    user.SetReferralCoupon(null);
                }

                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("User {UserId} upgraded to Pro, expires {Expires}", uid, periodEnd);
            }
        }
        else
        {
            logger.LogWarning("Could not extract userId from checkout session metadata");
        }
    }

    private async Task HandleInvoicePaid(Event stripeEvent, CancellationToken ct)
    {
        var invoice = stripeEvent.Data.Object as Invoice;
        var invoiceSubId = invoice?.Parent?.SubscriptionDetails?.SubscriptionId;
        logger.LogInformation("Invoice paid: subId={SubId}", invoiceSubId);

        if (invoiceSubId is not null)
        {
            var user = await userRepository.FindOneTrackedAsync(
                u => u.StripeSubscriptionId == invoiceSubId, cancellationToken: ct);
            if (user is not null)
            {
                var subscription = await subscriptionService.GetAsync(invoiceSubId, cancellationToken: ct);
                var periodEnd = subscription.Items?.Data?.Count > 0
                    ? subscription.Items.Data[0].CurrentPeriodEnd
                    : DateTime.UtcNow.AddMonths(1);
                user.SetStripeSubscription(invoiceSubId, periodEnd, GetSubscriptionInterval(subscription));
                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("User {UserId} subscription renewed, expires {Expires}", user.Id, periodEnd);
            }
        }
    }

    private async Task HandleSubscriptionDeleted(Event stripeEvent, CancellationToken ct)
    {
        var subscription = stripeEvent.Data.Object as Subscription;
        logger.LogInformation("Subscription deleted: {SubId}", subscription?.Id);
        if (subscription is not null)
        {
            var user = await userRepository.FindOneTrackedAsync(
                u => u.StripeSubscriptionId == subscription.Id, cancellationToken: ct);
            if (user is not null)
            {
                user.CancelSubscription();
                await unitOfWork.SaveChangesAsync(ct);
                logger.LogInformation("User {UserId} downgraded to Free", user.Id);
            }
        }
    }

    private async Task HandleSubscriptionUpdated(Event stripeEvent, CancellationToken ct)
    {
        var subscription = stripeEvent.Data.Object as Subscription;
        logger.LogInformation("Subscription updated: {SubId}, status={Status}", subscription?.Id, subscription?.Status);
        if (subscription is not null)
        {
            var user = await userRepository.FindOneTrackedAsync(
                u => u.StripeSubscriptionId == subscription.Id, cancellationToken: ct);
            if (user is not null)
            {
                if (subscription.Status == "active")
                {
                    var periodEnd = subscription.Items?.Data?.Count > 0
                        ? subscription.Items.Data[0].CurrentPeriodEnd
                        : DateTime.UtcNow.AddMonths(1);
                    user.SetStripeSubscription(subscription.Id, periodEnd, GetSubscriptionInterval(subscription));
                }
                else if (subscription.Status == "canceled" || subscription.Status == "unpaid")
                {
                    user.CancelSubscription();
                }
                await unitOfWork.SaveChangesAsync(ct);
            }
        }
    }

    private static SubscriptionInterval GetSubscriptionInterval(Subscription subscription)
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
