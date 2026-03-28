using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Referrals.Commands;

public record CheckReferralCompletionCommand(Guid UserId) : IRequest<Result>;

public class CheckReferralCompletionCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Referral> referralRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Notification> notificationRepository,
    IPushNotificationService pushNotificationService,
    IReferralRewardService referralRewardService,
    IUnitOfWork unitOfWork) : IRequestHandler<CheckReferralCompletionCommand, Result>
{
    public async Task<Result> Handle(CheckReferralCompletionCommand request, CancellationToken cancellationToken)
    {
        var pendingReferrals = await referralRepository.FindAsync(
            r => r.ReferredUserId == request.UserId && r.Status == ReferralStatus.Pending,
            cancellationToken: cancellationToken);

        var referral = pendingReferrals.FirstOrDefault();
        if (referral is null)
            return Result.Success();

        var trackedReferral = await referralRepository.FindOneTrackedAsync(
            r => r.Id == referral.Id,
            cancellationToken: cancellationToken);

        if (trackedReferral is null)
            return Result.Success();

        var referredUser = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (referredUser is null)
            return Result.Success();

        if (DateTime.UtcNow > referredUser.CreatedAtUtc.AddDays(AppConstants.ReferralCompletionWindowDays))
            return Result.Success();

        var userHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        var habitIds = userHabits.Select(h => h.Id).ToHashSet();
        if (habitIds.Count == 0)
            return Result.Success();

        var userLogs = await habitLogRepository.FindAsync(
            l => habitIds.Contains(l.HabitId),
            cancellationToken: cancellationToken);

        if (userLogs.Count < AppConstants.ReferralCompletionThreshold)
            return Result.Success();

        trackedReferral.MarkCompleted();

        // Grant coupon to referrer
        var referrer = await userRepository.FindOneTrackedAsync(
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
        var couponId = await referralRewardService.CreateReferralCouponAsync(
            user.Id, cancellationToken);

        if (user.IsPro && !string.IsNullOrEmpty(user.StripeSubscriptionId))
        {
            await referralRewardService.ApplyCouponToSubscriptionAsync(
                user.StripeSubscriptionId, couponId, cancellationToken);
        }
        else
        {
            user.SetReferralCoupon(couponId);
        }
    }

    private async Task SendNotification(User user, bool isReferrer, CancellationToken cancellationToken)
    {
        var isPt = user.Language?.StartsWith("pt") == true;

        var (title, body) = isReferrer
            ? (isPt ? "Indicacao Concluida!" : "Referral Completed!",
               isPt
                   ? user.IsPro
                       ? "Seu amigo comecou a usar o Orbit! 10% de desconto aplicado na sua proxima fatura."
                       : "Seu amigo comecou a usar o Orbit e voce ganhou um cupom de 10% de desconto no Pro!"
                   : user.IsPro
                       ? "Your friend joined Orbit! 10% discount applied to your next invoice."
                       : "Your friend joined Orbit and you earned a 10% discount coupon for Pro!")
            : (isPt ? "Voce ganhou um cupom!" : "You earned a coupon!",
               isPt
                   ? "Bem-vindo ao Orbit! Voce ganhou um cupom de 10% de desconto no Pro!"
                   : "Welcome to Orbit! You earned a 10% discount coupon for Pro!");

        await notificationRepository.AddAsync(
            Notification.Create(user.Id, title, body, "/profile"), cancellationToken);

        _ = Task.Run(async () =>
        {
            try { await pushNotificationService.SendToUserAsync(user.Id, title, body, "/profile", CancellationToken.None); }
            catch { }
        }, CancellationToken.None);
    }
}
