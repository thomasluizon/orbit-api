using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Infrastructure.Services;

public class StripeCouponRewardService(
    IGenericRepository<User> userRepository,
    ILogger<StripeCouponRewardService> logger) : IReferralRewardService
{
    private const string ProductId = "prod_UBUPrTlZg8chuk";

    public async Task<string> CreateReferralCouponAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);

        if (user is null)
            throw new InvalidOperationException($"User {userId} not found for coupon creation");

        var couponService = new CouponService();
        var coupon = await couponService.CreateAsync(new CouponCreateOptions
        {
            PercentOff = AppConstants.ReferralDiscountPercent,
            Duration = "once",
            MaxRedemptions = 1,
            Name = "Referral Discount",
            AppliesTo = new CouponAppliesToOptions
            {
                Products = [ProductId]
            }
        }, cancellationToken: cancellationToken);

        logger.LogInformation(
            "Created referral coupon for user {UserId}: couponId={CouponId}",
            userId, coupon.Id);

        return coupon.Id;
    }

    public async Task ApplyCouponToSubscriptionAsync(string subscriptionId, string couponId, CancellationToken cancellationToken = default)
    {
        var subscriptionService = new SubscriptionService();
        await subscriptionService.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
        {
            Discounts = [new SubscriptionDiscountOptions { Coupon = couponId }]
        }, cancellationToken: cancellationToken);

        logger.LogInformation(
            "Applied coupon {CouponId} to subscription {SubscriptionId}",
            couponId, subscriptionId);
    }
}
