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
    /// Clears the user's referral coupon and best-effort cancels it at the billing provider
    /// when the verified state shows the referral offer on a purchase token not yet linked to
    /// the user. The token-newness check makes renewals, re-verifies, and restores of an
    /// already-linked purchase no-ops, so callers MUST invoke this before overwriting
    /// <see cref="User.PlayPurchaseToken"/>. Never throws for billing-provider failures.
    /// </summary>
    Task ConsumeOnNewPurchaseAsync(User user, PlaySubscriptionState state, string purchaseToken, CancellationToken cancellationToken);
}
