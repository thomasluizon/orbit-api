using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Application.Subscriptions.Jobs;

public sealed class CancelPlayReferralCouponJob(IReferralRewardService rewards)
{
    public async Task ExecuteAsync(string couponId, CancellationToken cancellationToken)
    {
        try
        {
            await rewards.CancelCouponAsync(couponId, cancellationToken);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound
            || ex.StripeError?.Code == "resource_missing")
        {
            return;
        }
    }
}
