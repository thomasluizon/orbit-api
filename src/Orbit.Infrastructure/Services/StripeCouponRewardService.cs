using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Stripe;

namespace Orbit.Infrastructure.Services;

public partial class StripeCouponRewardService(
    IGenericRepository<User> userRepository,
    IOptions<StripeSettings> stripeSettings,
    ILogger<StripeCouponRewardService> logger) : IReferralRewardService
{
    private readonly StripeSettings _settings = stripeSettings.Value;

    public async Task<string> CreateReferralCouponAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == userId,
            cancellationToken: cancellationToken);

        if (user is null)
        {
            LogUserNotFoundForCoupon(logger, userId);
            throw new InvalidOperationException($"User {userId} not found for coupon creation");
        }

        var couponService = new CouponService();
        var coupon = await couponService.CreateAsync(new CouponCreateOptions
        {
            PercentOff = AppConstants.ReferralDiscountPercent,
            Duration = "once",
            MaxRedemptions = 1,
            Name = "Referral Discount",
            AppliesTo = !string.IsNullOrEmpty(_settings.ProProductId)
                ? new CouponAppliesToOptions { Products = [_settings.ProProductId] }
                : null
        }, cancellationToken: cancellationToken);

        LogCouponCreated(logger, userId, coupon.Id);

        return coupon.Id;
    }

    public async Task ApplyCouponToSubscriptionAsync(string subscriptionId, string couponId, CancellationToken cancellationToken = default)
    {
        var subscriptionService = new SubscriptionService();
        await subscriptionService.UpdateAsync(subscriptionId, new SubscriptionUpdateOptions
        {
            Discounts = [new SubscriptionDiscountOptions { Coupon = couponId }]
        }, cancellationToken: cancellationToken);

        LogCouponApplied(logger, couponId, subscriptionId);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "User {UserId} not found for referral coupon creation")]
    private static partial void LogUserNotFoundForCoupon(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Created referral coupon for user {UserId}: couponId={CouponId}")]
    private static partial void LogCouponCreated(ILogger logger, Guid userId, string couponId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Applied coupon {CouponId} to subscription {SubscriptionId}")]
    private static partial void LogCouponApplied(ILogger logger, string couponId, string subscriptionId);

}
