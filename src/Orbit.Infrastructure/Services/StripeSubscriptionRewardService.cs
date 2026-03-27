using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Infrastructure.Services;

public class StripeSubscriptionRewardService(
    ILogger<StripeSubscriptionRewardService> logger) : ISubscriptionRewardService
{
    public async Task ExtendSubscriptionAsync(string subscriptionId, int days, CancellationToken cancellationToken = default)
    {
        var subscriptionService = new SubscriptionService();
        var subscription = await subscriptionService.GetAsync(subscriptionId, cancellationToken: cancellationToken);

        // Calculate the new trial_end date:
        // If there's already a trial_end set (from a previous referral), extend from that.
        // Otherwise, extend from the current_period_end (next billing date).
        var baseDate = subscription.TrialEnd
            ?? subscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd
            ?? DateTime.UtcNow;

        var newTrialEnd = baseDate.AddDays(days);

        await subscriptionService.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
        {
            TrialEnd = newTrialEnd,
            ProrationBehavior = "none"
        }, cancellationToken: cancellationToken);

        logger.LogInformation(
            "Extended Stripe subscription {SubscriptionId} trial_end to {TrialEnd} (+{Days} days)",
            subscriptionId, newTrialEnd, days);
    }
}
