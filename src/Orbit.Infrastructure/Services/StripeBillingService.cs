using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Common;
using Stripe;
using Stripe.Checkout;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Stripe-backed implementation of <see cref="IBillingService"/>. All Stripe SDK calls
/// are isolated here so the Application layer stays SDK-free.
/// </summary>
/// <summary>Groups the Stripe SDK service clients the billing service uses to keep its constructor small.</summary>
public record StripeServiceClients(
    CustomerService Customers,
    Stripe.Checkout.SessionService CheckoutSessions,
    Stripe.BillingPortal.SessionService PortalSessions,
    SubscriptionService Subscriptions,
    InvoiceService Invoices,
    PriceService Prices,
    CouponService Coupons);

public sealed partial class StripeBillingService(
    StripeServiceClients clients,
    ILogger<StripeBillingService> logger) : IBillingService
{
    private const string RequestThreeDSecureWhenSupported = "any";

    public async Task<string> CreateCustomerAsync(string email, string name, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var customer = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Customers.CreateAsync(new CustomerCreateOptions
                {
                    Email = email,
                    Name = name,
                    Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString() },
                }, new RequestOptions { IdempotencyKey = $"orbit-customer-create-{userId}" }, cancellationToken),
                cancellationToken);
            return customer.Id;
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to create customer", ex);
        }
    }

    public async Task<string> CreateCheckoutSessionAsync(
        string customerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        Guid userId,
        string? referralCouponId,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = new SessionCreateOptions
            {
                Customer = customerId,
                Mode = "subscription",
                PaymentMethodOptions = new SessionPaymentMethodOptionsOptions
                {
                    Card = new SessionPaymentMethodOptionsCardOptions
                    {
                        RequestThreeDSecure = RequestThreeDSecureWhenSupported,
                    },
                },
                LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString() },
            };

            if (!string.IsNullOrEmpty(referralCouponId))
            {
                options.Discounts = [new SessionDiscountOptions { Coupon = referralCouponId }];
            }
            else
            {
                options.AllowPromotionCodes = true;
            }

            var idempotencyKey = CreateSessionIdempotencyKey($"orbit-checkout-{userId}",
                customerId, priceId, successUrl, cancelUrl, referralCouponId ?? "", RequestThreeDSecureWhenSupported);
            var session = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.CheckoutSessions.CreateAsync(
                    options, new RequestOptions { IdempotencyKey = idempotencyKey }, cancellationToken),
                cancellationToken);
            return session.Url;
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to create checkout session", ex);
        }
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken)
    {
        try
        {
            var idempotencyKey = CreateSessionIdempotencyKey("orbit-portal", customerId, returnUrl);
            var session = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.PortalSessions.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
                {
                    Customer = customerId,
                    ReturnUrl = returnUrl,
                }, new RequestOptions { IdempotencyKey = idempotencyKey }, cancellationToken),
                cancellationToken);
            return session.Url;
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to create portal session", ex);
        }
    }

    private static string CreateSessionIdempotencyKey(string prefix, params string[] parameters)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parameters)));
        return $"{prefix}-{Convert.ToHexStringLower(digest)[..16]}";
    }

    public async Task<BillingSubscriptionDetails?> GetSubscriptionDetailsAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            var options = new SubscriptionGetOptions();
            options.AddExpand("default_payment_method");
            var subscription = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Subscriptions.GetAsync(subscriptionId, options, cancellationToken: cancellationToken),
                cancellationToken);

            BillingPaymentMethod? paymentMethod = null;
            if (subscription.DefaultPaymentMethod?.Card is not null)
            {
                var card = subscription.DefaultPaymentMethod.Card;
                paymentMethod = new BillingPaymentMethod(
                    card.Brand ?? "unknown",
                    card.Last4 ?? "****",
                    (int)card.ExpMonth,
                    (int)card.ExpYear);
            }

            var item = subscription.Items?.Data?.FirstOrDefault();
            var intervalString = item?.Price?.Recurring?.Interval;
            var interval = intervalString == "year" ? SubscriptionInterval.Yearly : SubscriptionInterval.Monthly;

            return new BillingSubscriptionDetails(
                subscription.Status,
#pragma warning disable ORBIT0004 // BillingSubscriptionDetails.CurrentPeriodEnd falls back to the current instant when the Stripe item is missing; it is an expiry timestamp. https://github.com/thomasluizon/orbit-tickets/issues/230
                item?.CurrentPeriodEnd ?? DateTime.UtcNow,
#pragma warning restore ORBIT0004
                subscription.CancelAtPeriodEnd,
                interval,
                item?.Price?.UnitAmount ?? 0,
                subscription.Currency ?? "usd",
                paymentMethod,
                subscription.Metadata?.GetValueOrDefault(DeletionOwnerMetadataKey));
        }
        catch (StripeException ex) when (IsMissingSubscription(ex))
        {
            return null;
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            throw new BillingProviderException("Failed to fetch subscription", ex);
        }
    }

    public async Task<long?> TryGetRecurringAmountPerPeriodAsync(string subscriptionId, string currency, CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Invoices.CreatePreviewAsync(new InvoiceCreatePreviewOptions
                {
                    Subscription = subscriptionId,
                    PreviewMode = "recurring",
                }, cancellationToken: cancellationToken),
                cancellationToken);
            if (invoice.Currency != currency || invoice.Lines is not { HasMore: false, Data: not null })
            {
                LogRecurringPreviewIncomplete(logger, subscriptionId);
                return null;
            }

            var recurringLines = invoice.Lines.Data
                .Where(line => line.Parent?.SubscriptionItemDetails is { Proration: false } parent
                    && parent.Subscription == subscriptionId)
                .ToList();
            if (recurringLines.Count == 0 || recurringLines.Any(line => line.Currency != currency))
            {
                LogRecurringPreviewIncomplete(logger, subscriptionId);
                return null;
            }

            return recurringLines.Sum(line => line.Subtotal - (line.DiscountAmounts?.Sum(discount => discount.Amount) ?? 0));
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "invoice_upcoming_none")
        {
            return null;
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            LogRecurringPreviewFailed(logger, ex, subscriptionId);
            return null;
        }
    }

    public Task SetSubscriptionCancelAtPeriodEndAsync(
        string subscriptionId, bool cancelAtPeriodEnd, CancellationToken cancellationToken,
        string? deletionOwner = null) =>
        UpdateSubscriptionRenewalAsync(subscriptionId, cancelAtPeriodEnd, deletionOwner, cancellationToken);

    private const string DeletionOwnerMetadataKey = "orbit_deletion_user_id";

    private async Task<Subscription?> UpdateSubscriptionRenewalAsync(
        string subscriptionId, bool cancelAtPeriodEnd, string? deletionOwner, CancellationToken cancellationToken)
    {
        try
        {
            return await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Subscriptions.UpdateAsync(subscriptionId,
                    new SubscriptionUpdateOptions
                    {
                        CancelAtPeriodEnd = cancelAtPeriodEnd,
                        Metadata = new Dictionary<string, string>
                        {
                            [DeletionOwnerMetadataKey] = cancelAtPeriodEnd ? deletionOwner ?? "" : ""
                        }
                    },
                    cancellationToken: cancellationToken),
                cancellationToken);
        }
        catch (StripeException ex) when (IsMissingSubscription(ex))
        {
            return null;
        }
        catch (Exception ex) when (IsProviderFailure(ex, cancellationToken))
        {
            throw new BillingProviderException("Failed to update subscription renewal", ex);
        }
    }

    private static bool IsMissingSubscription(StripeException exception) =>
        exception.HttpStatusCode == System.Net.HttpStatusCode.NotFound
        || exception.StripeError?.Code == "resource_missing";

    private static bool IsProviderFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is StripeException or HttpRequestException or TimeoutException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    public async Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(string customerId, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var invoices = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Invoices.ListAsync(new InvoiceListOptions
                {
                    Customer = customerId,
                    Limit = limit,
                }, cancellationToken: cancellationToken),
                cancellationToken);

            return invoices.Data.Select(inv => new BillingInvoice(
                inv.Id,
                inv.Created,
                inv.AmountPaid,
                inv.Currency ?? "usd",
                inv.Status ?? "unknown",
                inv.HostedInvoiceUrl,
                inv.InvoicePdf,
                inv.BillingReason ?? "unknown")).ToList();
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException("Failed to list invoices", ex);
        }
    }

    public async Task<long> GetPriceUnitAmountAsync(string priceId, CancellationToken cancellationToken)
    {
        try
        {
            var price = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Prices.GetAsync(priceId, cancellationToken: cancellationToken),
                cancellationToken);
            return price.UnitAmount ?? 0;
        }
        catch (StripeException ex)
        {
            throw new BillingProviderException($"Failed to fetch price {priceId}", ex);
        }
    }

    public async Task<int?> TryGetCouponPercentOffAsync(string couponId, CancellationToken cancellationToken)
    {
        try
        {
            var coupon = await StripeRetryPolicy.ExecuteWithRetryAsync(
                () => clients.Coupons.GetAsync(couponId, cancellationToken: cancellationToken),
                cancellationToken);
            return (int)(coupon.PercentOff ?? 0);
        }
        catch (StripeException ex)
        {
            LogCouponFetchFailed(logger, ex, couponId);
            return null;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch coupon {CouponId}")]
    private static partial void LogCouponFetchFailed(ILogger logger, Exception ex, string couponId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to preview recurring amount for subscription {SubscriptionId}")]
    private static partial void LogRecurringPreviewFailed(ILogger logger, Exception ex, string subscriptionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Recurring preview is incomplete or has a different currency for subscription {SubscriptionId}")]
    private static partial void LogRecurringPreviewIncomplete(ILogger logger, string subscriptionId);
}
