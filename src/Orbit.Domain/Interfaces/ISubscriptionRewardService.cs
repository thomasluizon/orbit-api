namespace Orbit.Domain.Interfaces;

public interface ISubscriptionRewardService
{
    /// <summary>
    /// Extends a Pro user's Stripe subscription by delaying the next charge.
    /// Uses Stripe's trial_end to grant free days without breaking the billing cycle.
    /// </summary>
    Task ExtendSubscriptionAsync(string subscriptionId, int days, CancellationToken cancellationToken = default);
}
