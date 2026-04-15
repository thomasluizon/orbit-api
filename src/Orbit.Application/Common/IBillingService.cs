namespace Orbit.Application.Common;

/// <summary>
/// Payment-provider abstraction. All Stripe SDK calls for checkout, portal, billing
/// details, and plan pricing go through this interface. Implementations live in
/// Infrastructure so the Application layer has no compile-time dependency on Stripe.
///
/// HandleWebhookCommand.cs intentionally keeps its Stripe imports because webhook
/// payload shapes are defined by Stripe itself; decoupling it would require a full
/// domain-event model. It's the only remaining Stripe surface in Application.
/// </summary>
public interface IBillingService
{
    /// <summary>Create a Stripe customer and return the provider customer ID.</summary>
    Task<string> CreateCustomerAsync(
        string email,
        string name,
        Guid userId,
        CancellationToken cancellationToken);

    /// <summary>Create a checkout session and return the hosted URL the user should visit.</summary>
    Task<string> CreateCheckoutSessionAsync(
        string customerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        Guid userId,
        string? referralCouponId,
        CancellationToken cancellationToken);

    /// <summary>Create a billing-portal session and return the hosted URL.</summary>
    Task<string> CreatePortalSessionAsync(
        string customerId,
        string returnUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetch subscription details (status, period end, payment method) for display on the
    /// billing page. Returns null if the subscription cannot be found.
    /// </summary>
    Task<BillingSubscriptionDetails?> GetSubscriptionDetailsAsync(
        string subscriptionId,
        CancellationToken cancellationToken);

    /// <summary>List recent invoices for a customer.</summary>
    Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(
        string customerId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Fetch the unit amount (in provider minor units / cents) for a price ID.</summary>
    Task<long> GetPriceUnitAmountAsync(string priceId, CancellationToken cancellationToken);

    /// <summary>
    /// Look up a coupon's percent-off value. Returns null if the coupon cannot be fetched
    /// (missing, expired, or provider error); the caller should treat this as "no discount".
    /// </summary>
    Task<int?> TryGetCouponPercentOffAsync(string couponId, CancellationToken cancellationToken);
}

/// <summary>Subscription details surfaced on the billing-details endpoint.</summary>
public sealed record BillingSubscriptionDetails(
    string Status,
    DateTime CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    Orbit.Domain.Enums.SubscriptionInterval Interval,
    long UnitAmount,
    string Currency,
    BillingPaymentMethod? PaymentMethod);

public sealed record BillingPaymentMethod(string Brand, string Last4, int ExpMonth, int ExpYear);

public sealed record BillingInvoice(
    string Id,
    DateTime Created,
    long AmountPaid,
    string Currency,
    string Status,
    string? HostedUrl,
    string? PdfUrl,
    string BillingReason);

/// <summary>
/// Wraps transient and permanent billing-provider failures so Application code doesn't
/// need to import the Stripe SDK to catch them.
/// </summary>
public sealed class BillingProviderException : Exception
{
    public BillingProviderException(string message, Exception inner) : base(message, inner) { }
    public BillingProviderException(string message) : base(message) { }
}
