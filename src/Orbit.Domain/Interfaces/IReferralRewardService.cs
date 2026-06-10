namespace Orbit.Domain.Interfaces;

public interface IReferralRewardService
{
    /// <summary>
    /// Creates a one-time 10% discount coupon and returns the Stripe coupon ID.
    /// </summary>
    Task<string> CreateReferralCouponAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a coupon to an existing Stripe subscription's next invoice.
    /// </summary>
    Task ApplyCouponToSubscriptionAsync(string subscriptionId, string couponId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes an unredeemed coupon so it cannot be applied through another
    /// billing channel after being consumed (e.g., redeemed via a Google Play offer).
    /// </summary>
    Task CancelCouponAsync(string couponId, CancellationToken cancellationToken = default);
}
