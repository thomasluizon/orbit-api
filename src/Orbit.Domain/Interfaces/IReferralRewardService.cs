namespace Orbit.Domain.Interfaces;

public interface IReferralRewardService
{
    /// <summary>
    /// Ensures the user has a Stripe customer, creates a one-time 10% discount coupon,
    /// and returns the Stripe promotion code ID.
    /// </summary>
    Task<string> CreateReferralCouponAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the user's stored referral promotion code ID, or null if none exists.
    /// </summary>
    Task<string?> GetUserPromotionCodeAsync(Guid userId, CancellationToken cancellationToken = default);
}
