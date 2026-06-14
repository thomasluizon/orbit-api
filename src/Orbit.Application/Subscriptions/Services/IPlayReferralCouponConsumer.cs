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
    /// <summary>
    /// Clears the user's referral coupon pointer in memory when the verified state shows the
    /// referral offer on a purchase token not yet linked to the user, and returns the coupon id
    /// the caller must cancel at the billing provider via <see cref="CancelConsumedCouponAsync"/>
    /// after persisting the user mutation. Returns null when there is nothing to consume. The
    /// token-newness check makes renewals, re-verifies, and restores of an already-linked
    /// purchase no-ops, so callers MUST invoke this before overwriting
    /// <see cref="User.PlayPurchaseToken"/>.
    /// </summary>
    string? ConsumeOnNewPurchase(User user, PlaySubscriptionState state, string purchaseToken);

    /// <summary>
    /// Best-effort cancels a consumed referral coupon at the billing provider. Call only after the
    /// user mutation from <see cref="ConsumeOnNewPurchase"/> has been persisted, so a save failure
    /// can never leave a cancelled coupon with un-mutated user state. Never throws for
    /// billing-provider failures.
    /// </summary>
    Task CancelConsumedCouponAsync(Guid userId, string couponId, CancellationToken cancellationToken);
}
