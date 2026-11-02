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

}
