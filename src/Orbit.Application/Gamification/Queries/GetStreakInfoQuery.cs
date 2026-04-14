using MediatR;
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
    int MaxFreezesHeld,
    int StreakFreezeBalance,
    int DaysUntilNextFreeze,
    int ProgressToNextFreeze,
    bool IsAtHeldCap,
    bool IsFrozenToday,
    IReadOnlyList<DateOnly> RecentFreezeDates);

public record GetStreakInfoQuery(Guid UserId) : IRequest<Result<StreakInfoResponse>>;

public class GetStreakInfoQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService) : IRequestHandler<GetStreakInfoQuery, Result<StreakInfoResponse>>
{
    public async Task<Result<StreakInfoResponse>> Handle(GetStreakInfoQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<StreakInfoResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        // Calendar-month boundaries for usage cap (anchored in the user's timezone via today).
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);

        // Recent freezes for display: the last 30 days (unchanged from prior behavior).
        var recentStart = today.AddDays(-29);
        var recentWindowStart = recentStart < monthStart ? recentStart : monthStart;
        var recentFreezes = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= recentWindowStart,
            cancellationToken);

        var freezesThisMonth = recentFreezes.Count(sf => sf.UsedOnDate >= monthStart && sf.UsedOnDate < monthEnd);
        var isFrozenToday = recentFreezes.Any(sf => sf.UsedOnDate == today);
        var monthlyRemaining = Math.Max(0, AppConstants.MaxStreakFreezesPerMonth - freezesThisMonth);
        var freezesAvailable = Math.Min(user.StreakFreezeBalance, monthlyRemaining);
        var recentFreezeDates = recentFreezes
            .Where(sf => sf.UsedOnDate >= recentStart)
            .Select(sf => sf.UsedOnDate)
            .OrderByDescending(d => d)
            .ToList();

        var isAtHeldCap = user.StreakFreezeBalance >= AppConstants.MaxStreakFreezesHeld;
        var progressToNextFreeze = ComputeProgress(user);
        var daysUntilNextFreeze = isAtHeldCap
            ? AppConstants.DaysPerEarnedStreakFreeze
            : Math.Max(1, AppConstants.DaysPerEarnedStreakFreeze - progressToNextFreeze);

        return Result.Success(new StreakInfoResponse(
            user.CurrentStreak,
            user.LongestStreak,
            user.LastActiveDate,
            freezesThisMonth,
            freezesAvailable,
            AppConstants.MaxStreakFreezesPerMonth,
            AppConstants.MaxStreakFreezesHeld,
            user.StreakFreezeBalance,
            daysUntilNextFreeze,
            progressToNextFreeze,
            isAtHeldCap,
            isFrozenToday,
            recentFreezeDates));
    }

    private static int ComputeProgress(User user)
    {
        var delta = user.CurrentStreak - user.LastFreezeEarnedAtStreak;
        if (delta <= 0) return 0;
        return delta % AppConstants.DaysPerEarnedStreakFreeze;
    }
}
