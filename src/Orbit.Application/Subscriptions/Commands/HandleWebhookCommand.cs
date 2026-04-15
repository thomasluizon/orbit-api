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

public partial class HandleWebhookCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IOptions<StripeSettings> stripeSettings,
    SubscriptionService subscriptionService,
    ILogger<HandleWebhookCommandHandler> logger) : IRequestHandler<HandleWebhookCommand, Result>
{
    private readonly StripeSettings _settings = stripeSettings.Value;

    public async Task<Result> Handle(HandleWebhookCommand request, CancellationToken cancellationToken)
    {
        LogStripeWebhookReceived(logger, request.Json.Length);

        if (string.IsNullOrEmpty(_settings.WebhookSecret))
        {
            LogWebhookSecretNotConfigured(logger);
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
            LogWebhookSignatureVerificationFailed(logger, ex);
            return Result.Failure("Invalid webhook signature");
        }

        LogStripeEventType(logger, stripeEvent.Type, stripeEvent.Id);

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
        catch (StripeException ex)
        {
            // Stripe SDK threw -- could be a transient call to GetAsync. Surface as a
            // retryable failure so Stripe's webhook redelivery picks it up.
            LogErrorProcessingStripeEvent(logger, ex, stripeEvent.Type);
            return Result.Failure("Stripe API error processing webhook event");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-Stripe error (DB write, etc.). Generic message; do NOT include the raw
            // exception detail in the response body since Stripe will deliver it back to
            // the webhook caller.
            LogErrorProcessingStripeEvent(logger, ex, stripeEvent.Type);
            return Result.Failure("Webhook processing failed");
        }

        return Result.Success();
    }

    private async Task HandleCheckoutSessionCompleted(Event stripeEvent, CancellationToken ct)
    {
        var session = stripeEvent.Data.Object as Session;
        LogCheckoutSession(logger, session?.Id, session?.SubscriptionId ?? session?.Subscription?.Id,
            session?.Metadata != null ? string.Join(",", session.Metadata.Keys) : "null");

        var subscriptionId = session?.SubscriptionId ?? session?.Subscription?.Id;

        if (session?.Metadata?.TryGetValue("userId", out var userIdStr) != true
            || !Guid.TryParse(userIdStr, out var uid))
        {
            // Silent skip on missing/malformed userId metadata previously hid lost subscriptions.
            // Throw so the outer catch records the failure and Stripe retries the webhook with
            // (hopefully) corrected metadata; if metadata was set incorrectly upstream, the
            // operator will see the recurring error in logs instead of a silent data loss.
            LogCheckoutUserIdExtractionFailed(logger);
            throw new InvalidOperationException(
                $"checkout.session.completed missing or invalid userId metadata (session: {session?.Id ?? "null"}).");
        }

        var user = await userRepository.FindOneTrackedAsync(u => u.Id == uid, cancellationToken: ct);
        LogUserFound(logger, user is not null, subscriptionId);

        if (user is null || subscriptionId is null)
            return;

        var subscription = await subscriptionService.GetAsync(subscriptionId, cancellationToken: ct);
        var periodEnd = GetPeriodEnd(subscription);

        user.SetStripeCustomerId(session.CustomerId ?? session.Customer?.Id ?? "");
        user.SetStripeSubscription(subscriptionId, periodEnd, GetSubscriptionInterval(subscription));

        if (!string.IsNullOrEmpty(user.ReferralCouponId))
        {
            LogClearingReferralCoupon(logger, user.ReferralCouponId, uid);
            user.SetReferralCoupon(null);
        }

        await unitOfWork.SaveChangesAsync(ct);
        LogUserUpgradedToPro(logger, uid, periodEnd);
    }

    private async Task HandleInvoicePaid(Event stripeEvent, CancellationToken ct)
    {
        var invoice = stripeEvent.Data.Object as Invoice;
        var invoiceSubId = invoice?.Parent?.SubscriptionDetails?.SubscriptionId;
        LogInvoicePaid(logger, invoiceSubId);

        if (invoiceSubId is null)
            return;

        var user = await userRepository.FindOneTrackedAsync(
            u => u.StripeSubscriptionId == invoiceSubId, cancellationToken: ct);

        if (user is null)
            return;

        var subscription = await subscriptionService.GetAsync(invoiceSubId, cancellationToken: ct);
        var periodEnd = GetPeriodEnd(subscription);
        user.SetStripeSubscription(invoiceSubId, periodEnd, GetSubscriptionInterval(subscription));
        await unitOfWork.SaveChangesAsync(ct);
        LogSubscriptionRenewed(logger, user.Id, periodEnd);
    }

    private async Task HandleSubscriptionDeleted(Event stripeEvent, CancellationToken ct)
    {
        var subscription = stripeEvent.Data.Object as Subscription;
        LogSubscriptionDeleted(logger, subscription?.Id);

        if (subscription is null)
            return;

        var user = await userRepository.FindOneTrackedAsync(
            u => u.StripeSubscriptionId == subscription.Id, cancellationToken: ct);

        if (user is null)
            return;

        user.CancelSubscription();
        await unitOfWork.SaveChangesAsync(ct);
        LogUserDowngraded(logger, user.Id);
    }

    private async Task HandleSubscriptionUpdated(Event stripeEvent, CancellationToken ct)
    {
        var subscription = stripeEvent.Data.Object as Subscription;
        LogSubscriptionUpdated(logger, subscription?.Id, subscription?.Status);

        if (subscription is null)
            return;

        var user = await userRepository.FindOneTrackedAsync(
            u => u.StripeSubscriptionId == subscription.Id, cancellationToken: ct);

        if (user is null)
            return;

        if (subscription.Status == "active")
        {
            var periodEnd = GetPeriodEnd(subscription);
            user.SetStripeSubscription(subscription.Id, periodEnd, GetSubscriptionInterval(subscription));
        }
        else if (subscription.Status is "canceled" or "unpaid")
        {
            user.CancelSubscription();
        }

        await unitOfWork.SaveChangesAsync(ct);
    }

    private static DateTime GetPeriodEnd(Subscription subscription)
    {
        return subscription.Items?.Data?.Count > 0
            ? subscription.Items.Data[0].CurrentPeriodEnd
            : DateTime.UtcNow.AddMonths(1);
    }


    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Stripe webhook received, body length: {Length}")]
    private static partial void LogStripeWebhookReceived(ILogger logger, int length);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Stripe event type: {Type}, id: {Id}")]
    private static partial void LogStripeEventType(ILogger logger, string type, string id);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error processing Stripe webhook event {Type}")]
    private static partial void LogErrorProcessingStripeEvent(ILogger logger, Exception ex, string type);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Checkout session: id={Id}, subscription={SubId}, metadata keys={Keys}")]
    private static partial void LogCheckoutSession(ILogger logger, string? id, string? subId, string? keys);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "User found: {Found}, subscriptionId: {SubId}")]
    private static partial void LogUserFound(ILogger logger, bool found, string? subId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Clearing referral coupon {CouponId} for user {UserId} after checkout")]
    private static partial void LogClearingReferralCoupon(ILogger logger, string? couponId, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "User {UserId} upgraded to Pro, expires {Expires}")]
    private static partial void LogUserUpgradedToPro(ILogger logger, Guid userId, DateTime expires);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Invoice paid: subId={SubId}")]
    private static partial void LogInvoicePaid(ILogger logger, string? subId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "User {UserId} subscription renewed, expires {Expires}")]
    private static partial void LogSubscriptionRenewed(ILogger logger, Guid userId, DateTime expires);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Subscription deleted: {SubId}")]
    private static partial void LogSubscriptionDeleted(ILogger logger, string? subId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "User {UserId} downgraded to Free")]
    private static partial void LogUserDowngraded(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Subscription updated: {SubId}, status={Status}")]
    private static partial void LogSubscriptionUpdated(ILogger logger, string? subId, string? status);

    [LoggerMessage(EventId = 13, Level = LogLevel.Critical, Message = "Stripe WebhookSecret is not configured -- rejecting webhook")]
    private static partial void LogWebhookSecretNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 14, Level = LogLevel.Error, Message = "Stripe webhook signature verification failed")]
    private static partial void LogWebhookSignatureVerificationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "Could not extract userId from checkout session metadata")]
    private static partial void LogCheckoutUserIdExtractionFailed(ILogger logger);

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
