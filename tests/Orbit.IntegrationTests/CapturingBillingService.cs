using Orbit.Application.Common;

namespace Orbit.IntegrationTests;

/// <summary>
/// Test double for <see cref="IBillingService"/> that removes the live-Stripe dependency from
/// checkout and plans integration tests. It records the price ID passed to checkout (so a test
/// can assert which price the resolver selected) and returns deterministic unit amounts for the
/// two price IDs requested by the plans endpoint. Tests run in the Sequential collection, so the
/// single captured price ID is read back right after each checkout call with no cross-test race.
/// </summary>
public sealed class CapturingBillingService : IBillingService
{
    private const long MonthlyUnitAmount = 1990;
    private const long YearlyUnitAmount = 19900;

    public string? LastCheckoutPriceId { get; private set; }

    public Task<string> CreateCustomerAsync(string email, string name, Guid userId, CancellationToken cancellationToken)
        => Task.FromResult($"cus_test_{userId:N}");

    public Task<string> CreateCheckoutSessionAsync(
        string customerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        Guid userId,
        string? referralCouponId,
        CancellationToken cancellationToken)
    {
        LastCheckoutPriceId = priceId;
        return Task.FromResult($"https://checkout.stripe.test/{priceId}");
    }

    public Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken cancellationToken)
        => Task.FromResult("https://portal.stripe.test/session");

    public Task<BillingSubscriptionDetails?> GetSubscriptionDetailsAsync(string subscriptionId, CancellationToken cancellationToken)
        => Task.FromResult<BillingSubscriptionDetails?>(null);

    public Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(string customerId, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BillingInvoice>>([]);

    public Task<long> GetPriceUnitAmountAsync(string priceId, CancellationToken cancellationToken)
        => Task.FromResult(priceId.Contains("yearly", StringComparison.Ordinal) ? YearlyUnitAmount : MonthlyUnitAmount);

    public Task<int?> TryGetCouponPercentOffAsync(string couponId, CancellationToken cancellationToken)
        => Task.FromResult<int?>(null);
}
