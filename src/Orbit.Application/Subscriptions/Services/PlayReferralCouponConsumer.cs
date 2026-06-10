using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Subscriptions.Services;

public partial class PlayReferralCouponConsumer(
    IOptions<GooglePlaySettings> playSettings,
    IReferralRewardService referralRewardService,
    ILogger<PlayReferralCouponConsumer> logger) : IPlayReferralCouponConsumer
{
    private readonly GooglePlaySettings _settings = playSettings.Value;

    public async Task ConsumeOnNewPurchaseAsync(User user, PlaySubscriptionState state, string purchaseToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ReferralOfferId))
            return;

        if (string.Equals(user.PlayPurchaseToken, purchaseToken, StringComparison.Ordinal))
            return;

        if (!string.Equals(state.OfferId, _settings.ReferralOfferId, StringComparison.OrdinalIgnoreCase))
            return;

        var couponId = user.ReferralCouponId;
        if (string.IsNullOrEmpty(couponId))
        {
            LogReferralOfferWithoutCoupon(logger, user.Id);
            return;
        }

        user.SetReferralCoupon(null);
        LogConsumedReferralCoupon(logger, couponId, user.Id);

        try
        {
            await referralRewardService.CancelCouponAsync(couponId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCancelCouponFailed(logger, ex, couponId, user.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Consumed referral coupon {CouponId} for user {UserId} via Play referral offer")]
    private static partial void LogConsumedReferralCoupon(ILogger logger, string couponId, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Play purchase used the referral offer but user {UserId} holds no referral coupon")]
    private static partial void LogReferralOfferWithoutCoupon(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to cancel referral coupon {CouponId} for user {UserId}; user pointer already cleared")]
    private static partial void LogCancelCouponFailed(ILogger logger, Exception ex, string couponId, Guid userId);
}
