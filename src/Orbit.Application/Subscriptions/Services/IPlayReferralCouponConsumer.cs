using Orbit.Application.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Subscriptions.Services;

/// <summary>
/// Consumes a user's referral coupon when a verified Play purchase used the configured
/// referral discount offer. Mirrors the Stripe-side consumption done by the
/// checkout.session.completed webhook.
/// </summary>
public interface IPlayReferralCouponConsumer
{
    string? ConsumeOnNewPurchase(User user, PlaySubscriptionState state, string purchaseToken);

    /// <summary>
    /// Best-effort cancels a consumed referral coupon at the billing provider. Call only after the
    /// user mutation from <see cref="ConsumeOnNewPurchase"/> has been persisted, so a save failure
    /// can never leave a cancelled coupon with un-mutated user state. Never throws for
    /// billing-provider failures.
    /// </summary>
    Task CancelConsumedCouponAsync(Guid userId, string couponId, CancellationToken cancellationToken);
}
