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

        // Query recent freezes within the rolling 30-day window
        var windowStart = today.AddDays(-29);
        var recentFreezes = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= windowStart,
            cancellationToken);

        var isFrozenToday = recentFreezes.Any(sf => sf.UsedOnDate == today);
        var freezesUsed = recentFreezes.Count;
        var freezesAvailable = Math.Max(0, AppConstants.MaxStreakFreezesPerMonth - freezesUsed);
        var recentFreezeDates = recentFreezes
            .Select(sf => sf.UsedOnDate)
            .OrderByDescending(d => d)
            .ToList();

        return Result.Success(new StreakInfoResponse(
            user.CurrentStreak,
            user.LongestStreak,
            user.LastActiveDate,
            freezesUsed,
            freezesAvailable,
            AppConstants.MaxStreakFreezesPerMonth,
            isFrozenToday,
            recentFreezeDates));
    }
}
