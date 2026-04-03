using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Referrals.Commands;

public record CheckReferralCompletionCommand(Guid UserId) : IRequest<Result>;

/// <summary>
/// Groups repository dependencies for referral handling to reduce constructor parameter count (S107).
/// </summary>
public record ReferralRepositories(
    IGenericRepository<User> UserRepository,
    IGenericRepository<Referral> ReferralRepository,
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<HabitLog> HabitLogRepository,
    IGenericRepository<Notification> NotificationRepository);

public partial class CheckReferralCompletionCommandHandler(
    ReferralRepositories repos,
    IPushNotificationService pushNotificationService,
    IReferralRewardService referralRewardService,
    IUnitOfWork unitOfWork,
    ILogger<CheckReferralCompletionCommandHandler> logger) : IRequestHandler<CheckReferralCompletionCommand, Result>
{
    public async Task<Result> Handle(CheckReferralCompletionCommand request, CancellationToken cancellationToken)
    {
        var pendingReferrals = await repos.ReferralRepository.FindAsync(
            r => r.ReferredUserId == request.UserId && r.Status == ReferralStatus.Pending,
            cancellationToken: cancellationToken);

        var referral = pendingReferrals.Count > 0 ? pendingReferrals[0] : null;
        if (referral is null)
            return Result.Success();

        var trackedReferral = await repos.ReferralRepository.FindOneTrackedAsync(
            r => r.Id == referral.Id,
            cancellationToken: cancellationToken);

        if (trackedReferral is null)
            return Result.Success();

        var referredUser = await repos.UserRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (referredUser is null)
            return Result.Success();

        if (DateTime.UtcNow > referredUser.CreatedAtUtc.AddDays(AppConstants.ReferralCompletionWindowDays))
            return Result.Success();

        var userHabits = await repos.HabitRepository.FindAsync(
            h => h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        var habitIds = userHabits.Select(h => h.Id).ToHashSet();
        if (habitIds.Count == 0)
            return Result.Success();

        var userLogs = await repos.HabitLogRepository.FindAsync(
            l => habitIds.Contains(l.HabitId),
            cancellationToken: cancellationToken);

        if (userLogs.Count < AppConstants.ReferralCompletionThreshold)
            return Result.Success();

        trackedReferral.MarkCompleted();

        // Grant coupon to referrer
        var referrer = await repos.UserRepository.FindOneTrackedAsync(
            u => u.Id == trackedReferral.ReferrerId,
            cancellationToken: cancellationToken);

        if (referrer is not null)
            await GrantCoupon(referrer, cancellationToken);

        // Grant coupon to referred user
        await GrantCoupon(referredUser, cancellationToken);

        trackedReferral.MarkRewarded();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Send notifications
        if (referrer is not null)
            await SendNotification(referrer, isReferrer: true, cancellationToken);

        await SendNotification(referredUser, isReferrer: false, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// If user is Pro with active subscription, apply coupon to next invoice directly.
    /// Otherwise, store coupon ID for checkout.
    /// </summary>
    private async Task GrantCoupon(User user, CancellationToken cancellationToken)
    {
        string couponId;
        try
        {
            couponId = await referralRewardService.CreateReferralCouponAsync(
                user.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            LogCreateReferralCouponFailed(logger, ex, user.Id);
            return;
        }

        if (user.IsPro && !string.IsNullOrEmpty(user.StripeSubscriptionId))
        {
            try
            {
                await referralRewardService.ApplyCouponToSubscriptionAsync(
                    user.StripeSubscriptionId, couponId, cancellationToken);
            }
            catch (Exception ex)
            {
                LogApplyReferralCouponFailed(logger, ex, couponId, user.StripeSubscriptionId, user.Id);
                // Fall back to storing coupon for next checkout
                user.SetReferralCoupon(couponId);
            }
        }
        else
        {
            user.SetReferralCoupon(couponId);
        }
    }

    private async Task SendNotification(User user, bool isReferrer, CancellationToken cancellationToken)
    {
        var isPt = user.Language?.StartsWith("pt") == true;

        var (title, body) = GetNotificationContent(isReferrer, isPt, user.IsPro);

        await repos.NotificationRepository.AddAsync(
            Notification.Create(user.Id, title, body, "/profile"), cancellationToken);

        _ = Task.Run(async () =>
        {
            try { await pushNotificationService.SendToUserAsync(user.Id, title, body, "/profile", CancellationToken.None); }
            catch (Exception ex) { LogReferralPushNotificationFailed(logger, ex, user.Id); }
        }, CancellationToken.None);
    }

    private static (string Title, string Body) GetNotificationContent(bool isReferrer, bool isPt, bool isPro)
    {
        if (isReferrer)
        {
            var title = isPt ? "Indica\u00e7\u00e3o Conclu\u00edda!" : "Referral Completed!";
            var body = (isPt, isPro) switch
            {
                (true, true) => "Seu amigo come\u00e7ou a usar o Orbit! 10% de desconto aplicado na sua pr\u00f3xima fatura.",
                (true, false) => "Seu amigo come\u00e7ou a usar o Orbit e voc\u00ea ganhou um cupom de 10% de desconto no Pro!",
                (false, true) => "Your friend joined Orbit! 10% discount applied to your next invoice.",
                (false, false) => "Your friend joined Orbit and you earned a 10% discount coupon for Pro!"
            };
            return (title, body);
        }
        else
        {
            var title = isPt ? "Voc\u00ea ganhou um cupom!" : "You earned a coupon!";
            var body = isPt
                ? "Boas-vindas ao Orbit! Voc\u00ea ganhou um cupom de 10% de desconto no Pro!"
                : "Welcome to Orbit! You earned a 10% discount coupon for Pro!";
            return (title, body);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to create referral coupon for user {UserId}")]
    private static partial void LogCreateReferralCouponFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to apply referral coupon {CouponId} to subscription {SubscriptionId} for user {UserId}")]
    private static partial void LogApplyReferralCouponFailed(ILogger logger, Exception ex, string couponId, string? subscriptionId, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed to send referral push notification for user {UserId}")]
    private static partial void LogReferralPushNotificationFailed(ILogger logger, Exception ex, Guid userId);
}
