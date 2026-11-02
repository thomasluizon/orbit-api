using Hangfire;
using Orbit.Application.Subscriptions.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Services;

public partial class PlayReferralCouponConsumer(
    IOptions<GooglePlaySettings> playSettings,
    IBackgroundJobClient backgroundJobs,
    ILogger<PlayReferralCouponConsumer> logger) : IPlayReferralCouponConsumer
{
    private readonly GooglePlaySettings _settings = playSettings.Value;

    public string? ConsumeOnNewPurchase(User user, PlaySubscriptionState state, string purchaseToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ReferralOfferId))
            return null;

        if (string.Equals(user.PlayPurchaseToken, purchaseToken, StringComparison.Ordinal))
            return null;

        if (!string.Equals(state.OfferId, _settings.ReferralOfferId, StringComparison.OrdinalIgnoreCase))
            return null;

        var couponId = user.ReferralCouponId;
        if (string.IsNullOrEmpty(couponId))
        {
            LogReferralOfferWithoutCoupon(logger, user.Id);
            return null;
        }

        user.SetReferralCoupon(null);
        LogConsumedReferralCoupon(logger, couponId, user.Id);
        return couponId;
    }

    public Task CancelConsumedCouponAsync(Guid userId, string couponId, CancellationToken cancellationToken)
    {
        backgroundJobs.Enqueue<CancelPlayReferralCouponJob>(job => job.ExecuteAsync(couponId, CancellationToken.None));
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Consumed referral coupon {CouponId} for user {UserId} via Play referral offer")]
    private static partial void LogConsumedReferralCoupon(ILogger logger, string couponId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Play purchase used the referral offer but user {UserId} holds no referral coupon")]
    private static partial void LogReferralOfferWithoutCoupon(ILogger logger, Guid userId);

}
