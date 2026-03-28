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
        // Find pending referral where this user is the referred user
        var pendingReferrals = await referralRepository.FindAsync(
            r => r.ReferredUserId == request.UserId && r.Status == ReferralStatus.Pending,
            cancellationToken: cancellationToken);

        var referral = pendingReferrals.FirstOrDefault();
        if (referral is null)
            return Result.Success(); // No pending referral, nothing to do

        // Need tracked entity for state changes
        var trackedReferral = await referralRepository.FindOneTrackedAsync(
            r => r.Id == referral.Id,
            cancellationToken: cancellationToken);

        if (trackedReferral is null)
            return Result.Success();

        // Load the referred user to check signup date
        var referredUser = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (referredUser is null)
            return Result.Success();

        // Check if within the completion window
        if (DateTime.UtcNow > referredUser.CreatedAtUtc.AddDays(AppConstants.ReferralCompletionWindowDays))
            return Result.Success(); // Window expired, leave as Pending

        // Count total habit logs for this user
        // HabitLog doesn't have UserId, so first get user's habit IDs, then count logs
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
            return Result.Success(); // Not enough logs yet

        // Mark referral as completed
        trackedReferral.MarkCompleted();

        // Grant discount coupon to both referrer and referred user
        var referrer = await userRepository.FindOneTrackedAsync(
            u => u.Id == trackedReferral.ReferrerId,
            cancellationToken: cancellationToken);

        if (referrer is not null)
        {
            var referrerPromoId = await referralRewardService.CreateReferralCouponAsync(
                referrer.Id, cancellationToken);
            referrer.SetReferralCoupon(referrerPromoId);
        }

        // Referred user also gets a coupon
        var referredPromoId = await referralRewardService.CreateReferralCouponAsync(
            referredUser.Id, cancellationToken);
        referredUser.SetReferralCoupon(referredPromoId);

        trackedReferral.MarkRewarded();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Send notifications to both users
        if (referrer is not null)
        {
            var isPtReferrer = referrer.Language?.StartsWith("pt") == true;
            var referrerTitle = isPtReferrer ? "Indicacao Concluida!" : "Referral Completed!";
            var referrerBody = isPtReferrer
                ? "Seu amigo comecou a usar o Orbit e voce ganhou um cupom de 10% de desconto no Pro!"
                : "Your friend joined Orbit and you earned a 10% discount coupon for Pro!";

            await notificationRepository.AddAsync(
                Notification.Create(referrer.Id, referrerTitle, referrerBody, "/profile"), cancellationToken);

            _ = Task.Run(async () =>
            {
                try { await pushNotificationService.SendToUserAsync(referrer.Id, referrerTitle, referrerBody, "/profile", CancellationToken.None); }
                catch { }
            }, CancellationToken.None);
        }

        // Notify referred user they earned a coupon too
        if (referredUser is not null)
        {
            var isPtReferred = referredUser.Language?.StartsWith("pt") == true;
            var referredTitle = isPtReferred ? "Voce ganhou um cupom!" : "You earned a coupon!";
            var referredBody = isPtReferred
                ? "Bem-vindo ao Orbit! Voce ganhou um cupom de 10% de desconto no Pro!"
                : "Welcome to Orbit! You earned a 10% discount coupon for Pro!";

            await notificationRepository.AddAsync(
                Notification.Create(referredUser.Id, referredTitle, referredBody, "/profile"), cancellationToken);

            _ = Task.Run(async () =>
            {
                try { await pushNotificationService.SendToUserAsync(referredUser.Id, referredTitle, referredBody, "/profile", CancellationToken.None); }
                catch { }
            }, CancellationToken.None);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
