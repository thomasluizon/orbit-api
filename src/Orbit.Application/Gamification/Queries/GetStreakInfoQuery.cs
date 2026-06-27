using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Queries;

public record StreakInfoResponse(
    int CurrentStreak,
    int LongestStreak,
    DateOnly? LastActiveDate,
    int FreezesUsedThisMonth,
    int FreezesAvailable,
    int MaxFreezesPerMonth,
    bool IsFrozenToday,
    IReadOnlyList<DateOnly> RecentFreezeDates,
    int StreakFreezesAccumulated,
    int MaxStreakFreezesAccumulated,
    int DaysUntilNextFreeze,
    int FreezesAvailableToUse,
    bool CanEarnMore);

public record GetStreakInfoQuery(Guid UserId) : IRequest<Result<StreakInfoResponse>>, IConcurrencyRetryable;

public class GetStreakInfoQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService,
    IUserStreakService userStreakService,
    IFeatureFlagService featureFlagService,
    IUnitOfWork unitOfWork) : IRequestHandler<GetStreakInfoQuery, Result<StreakInfoResponse>>
{
    public async Task<Result<StreakInfoResponse>> Handle(GetStreakInfoQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<StreakInfoResponse>(ErrorMessages.UserNotFound);

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
        var unlocked = user.HasProAccess || enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier);
        if (!unlocked)
            return Result.PayGateFailure<StreakInfoResponse>("Streak insights are a Pro feature. Upgrade to unlock!");

        var recalculatedStreak = await userStreakService.RecalculateAsync(
            request.UserId, cancellationToken, awardFreezeIfEligible: false);
        if (recalculatedStreak is not null)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        var currentStreak = recalculatedStreak?.CurrentStreak ?? user.CurrentStreak;
        var longestStreak = recalculatedStreak?.LongestStreak ?? user.LongestStreak;
        var lastActiveDate = recalculatedStreak?.LastActiveDate ?? user.LastActiveDate;

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var freezesThisMonth = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= monthStart && sf.UsedOnDate < monthEnd,
            cancellationToken);

        var windowStart = today.AddDays(-29);
        var recentFreezes = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= windowStart,
            cancellationToken);

        var isFrozenToday = recentFreezes.Any(sf => sf.UsedOnDate == today);
        var freezesUsedThisMonth = freezesThisMonth.Count;
        var remainingMonthlyQuota = Math.Max(0, AppConstants.MaxStreakFreezesPerMonth - freezesUsedThisMonth);

        var freezesAvailableToUse = Math.Min(user.StreakFreezesAccumulated, remainingMonthlyQuota);

        var daysSinceLastAward = Math.Max(0, currentStreak - user.LastFreezeAwardStreak);
        var daysUntilNextFreeze = currentStreak <= 0
            ? AppConstants.StreakDaysPerFreeze
            : Math.Max(0, AppConstants.StreakDaysPerFreeze - (daysSinceLastAward % AppConstants.StreakDaysPerFreeze));
        if (daysUntilNextFreeze == 0 && currentStreak > 0 && user.StreakFreezesAccumulated >= AppConstants.MaxStreakFreezesAccumulated)
        {
            daysUntilNextFreeze = AppConstants.StreakDaysPerFreeze;
        }

        var canEarnMore = user.StreakFreezesAccumulated < AppConstants.MaxStreakFreezesAccumulated;

        var recentFreezeDates = recentFreezes
            .Select(sf => sf.UsedOnDate)
            .OrderByDescending(d => d)
            .ToList();

        return Result.Success(new StreakInfoResponse(
            currentStreak,
            longestStreak,
            lastActiveDate,
            freezesUsedThisMonth,
            user.StreakFreezesAccumulated,
            AppConstants.MaxStreakFreezesPerMonth,
            isFrozenToday,
            recentFreezeDates,
            user.StreakFreezesAccumulated,
            AppConstants.MaxStreakFreezesAccumulated,
            daysUntilNextFreeze,
            freezesAvailableToUse,
            canEarnMore));
    }
}
