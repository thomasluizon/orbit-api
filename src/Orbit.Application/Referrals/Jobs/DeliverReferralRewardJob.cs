using Hangfire;
using Orbit.Application.Common;
using Orbit.Application.Notifications;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Referrals.Jobs;

public sealed class DeliverReferralRewardJob(
    ReferralRepositories repos,
    IReferralRewardService rewards,
    IPushNotificationService push,
    IUnitOfWork unitOfWork)
{
    [AutomaticRetry(Attempts = 26, DelaysInSeconds = new[] { 60, 300, 3600 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task ExecuteAsync(Guid referralId, Guid userId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"referral-delivery:{referralId}:{userId}", ct);
            var referral = await repos.ReferralRepository.FindOneTrackedAsync(r => r.Id == referralId, cancellationToken: ct);
            if (referral is null)
                return;
            await repos.ReferralRepository.ReloadAsync(referral, ct);
            if (referral.Status == ReferralStatus.Pending)
                throw new InvalidOperationException("Referral reward has not committed yet.");
            var kind = referral.GetReward(userId);
            if (kind == ReferralRewardKind.None || referral.IsRewardDelivered(userId))
                return;

            var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == userId, cancellationToken: ct);
            if (user is null)
                return;
            await repos.UserRepository.ReloadAsync(user, ct);
            if (kind == ReferralRewardKind.Coupon)
            {
                var couponId = await rewards.CreateReferralCouponAsync(userId, $"referral-reward-{referralId}-{userId}", ct);
                if (user.HasActivePaidSubscription && !string.IsNullOrWhiteSpace(user.StripeSubscriptionId))
                    await rewards.ApplyCouponToSubscriptionAsync(user.StripeSubscriptionId, couponId, ct);
                else
                    user.SetReferralCoupon(couponId);
            }

            var (title, body) = GetNotificationContent(user, userId == referral.ReferrerId, kind);
            await repos.NotificationRepository.AddAsync(Notification.Create(userId, title, body, NotificationUrls.Profile), ct);
            await push.SendToUserAsync(userId, title, body, NotificationUrls.Profile, ct);
            referral.MarkRewardDelivered(userId);
            await unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);

    private static (string Title, string Body) GetNotificationContent(User user, bool isReferrer, ReferralRewardKind kind)
    {
        var isPt = LocaleHelper.IsPortuguese(user.Language);
        var title = isReferrer ? (isPt ? "Indicação concluída" : "Referral completed")
            : (isPt ? "Recompensa de indicação" : "Referral reward");
        var body = kind == ReferralRewardKind.ProDays
            ? (isPt ? $"Você ganhou {AppConstants.ReferralBonusProDays} dias de Pro."
                : $"You earned {AppConstants.ReferralBonusProDays} days of Pro.")
            : (isPt ? $"Você ganhou um cupom de {AppConstants.ReferralDiscountPercent}% de desconto no Pro."
                : $"You earned a {AppConstants.ReferralDiscountPercent}% discount coupon for Pro.");
        return (title, body);
    }
}
