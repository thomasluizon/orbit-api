using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Jobs;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Referrals.Commands;

public record CheckReferralCompletionCommand(Guid UserId) : IRequest<Result>;

/// <summary>Repository dependencies shared by referral qualification and delivery.</summary>
public record ReferralRepositories(
    IGenericRepository<User> UserRepository,
    IGenericRepository<Referral> ReferralRepository,
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<HabitLog> HabitLogRepository,
    IGenericRepository<Notification> NotificationRepository);

public class CheckReferralCompletionCommandHandler(
    ReferralRepositories repos,
    IBackgroundJobClient jobs,
    IUserDateService dates,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<CheckReferralCompletionCommand, Result>
{
    public async Task<Result> Handle(CheckReferralCompletionCommand request, CancellationToken cancellationToken)
    {
        var pending = await repos.ReferralRepository.FindAsync(
            r => r.ReferredUserId == request.UserId && r.Status == ReferralStatus.Pending && r.RewardGrantedAtUtc == null,
            cancellationToken);
        var referral = pending.FirstOrDefault();
        if (referral is null)
            return Result.Success();

        Referral? changedReferral = null;
        var changedUsers = new List<User>();
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                foreach (var userId in new[] { referral.ReferrerId, referral.ReferredUserId }.Order())
                    await unitOfWork.AcquireAdvisoryLockAsync($"referral-rewards:{userId}", ct);

                var tracked = await repos.ReferralRepository.FindOneTrackedAsync(r => r.Id == referral.Id, cancellationToken: ct);
                if (tracked is null)
                    return;
                await repos.ReferralRepository.ReloadAsync(tracked, ct);
                if (tracked.Status != ReferralStatus.Pending || tracked.RewardGrantedAtUtc is not null)
                    return;

                var referred = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: ct);
                if (referred is null)
                    return;
                await repos.UserRepository.ReloadAsync(referred, ct);
                if (!await QualifiesAsync(tracked, referred, ct))
                    return;

                var referrer = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == tracked.ReferrerId, cancellationToken: ct);
                if (referrer is not null)
                    await repos.UserRepository.ReloadAsync(referrer, ct);
                var referrerReward = await GetRewardAsync(referrer, ct);
                var referredReward = await GetRewardAsync(referred, ct);

                if (referrerReward != ReferralRewardKind.None || referredReward != ReferralRewardKind.None)
                {
                    // Enqueue before commit so queue failure rolls back the grant: https://docs.hangfire.io/en/latest/background-methods/calling-methods-in-background.html
                    if (referrerReward != ReferralRewardKind.None)
                        jobs.Enqueue<DeliverReferralRewardJob>(job => job.ExecuteAsync(tracked.Id, tracked.ReferrerId, CancellationToken.None));
                    if (referredReward != ReferralRewardKind.None)
                        jobs.Enqueue<DeliverReferralRewardJob>(job => job.ExecuteAsync(tracked.Id, tracked.ReferredUserId, CancellationToken.None));

                    changedReferral = tracked;
                    if (referrer is not null && referrerReward == ReferralRewardKind.ProDays)
                    {
                        changedUsers.Add(referrer);
                        referrer.GrantBonusProDays(AppConstants.ReferralBonusProDays);
                    }
                    if (referredReward == ReferralRewardKind.ProDays)
                    {
                        changedUsers.Add(referred);
                        referred.GrantBonusProDays(AppConstants.ReferralBonusProDays);
                    }
                    tracked.MarkRewarded(referrerReward, referredReward);
                }
                else
                {
                    changedReferral = tracked;
                    tracked.MarkCompleted();
                }
                await unitOfWork.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await ReloadRewardStateAsync(changedReferral, changedUsers, cancellationToken);
        }
        catch
        {
            await ReloadRewardStateAsync(changedReferral, changedUsers, cancellationToken);
            throw;
        }
        return Result.Success();
    }

    private async Task ReloadRewardStateAsync(Referral? referral, List<User> users, CancellationToken ct)
    {
        if (referral is not null)
            await repos.ReferralRepository.ReloadAsync(referral, ct);
        foreach (var user in users)
            await repos.UserRepository.ReloadAsync(user, ct);
    }

    private async Task<bool> QualifiesAsync(Referral referral, User referred, CancellationToken ct)
    {
        var nowAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var windowEndsAtUtc = referred.CreatedAtUtc.AddDays(AppConstants.ReferralCompletionWindowDays);
        if (nowAtUtc > windowEndsAtUtc || nowAtUtc < referral.CreatedAtUtc)
            return false;

        var timeZone = await dates.GetUserTimeZoneAsync(referred.Id, ct);
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(referral.CreatedAtUtc, timeZone));
        var today = await dates.GetUserTodayAsync(referred.Id, ct);
        var habits = await repos.HabitRepository.FindAsync(h => h.UserId == referred.Id, ct);
        var habitIds = habits.Select(h => h.Id).ToArray();
        if (habitIds.Length == 0)
            return false;

        var logDates = await repos.HabitLogRepository.ProjectAsync(
            l => habitIds.Contains(l.HabitId) && !l.IsDeleted && !l.IsSlip && l.Value > 0
                && l.Date >= firstDate && l.Date <= today
                && l.CreatedAtUtc >= referral.CreatedAtUtc && l.CreatedAtUtc <= nowAtUtc && l.CreatedAtUtc <= windowEndsAtUtc,
            q => q.Select(l => l.Date).Distinct().Take(AppConstants.ReferralCompletionThreshold), ct);
        return logDates.Count >= AppConstants.ReferralCompletionThreshold;
    }

    private async Task<ReferralRewardKind> GetRewardAsync(User? user, CancellationToken ct)
    {
        if (user is null)
            return ReferralRewardKind.None;
        var earned = await repos.ReferralRepository.CountAsync(
            r => r.RewardGrantedAtUtc != null
                && (r.ReferrerId == user.Id && (r.ReferrerReward != ReferralRewardKind.None || r.ReferredReward == ReferralRewardKind.None)
                    || r.ReferredUserId == user.Id && (r.ReferredReward != ReferralRewardKind.None || r.ReferrerReward == ReferralRewardKind.None)), ct);
        if (earned >= AppConstants.ReferralRewardCap)
            return ReferralRewardKind.None;
        return user.HasActivePaidSubscription ? ReferralRewardKind.Coupon : ReferralRewardKind.ProDays;
    }
}
