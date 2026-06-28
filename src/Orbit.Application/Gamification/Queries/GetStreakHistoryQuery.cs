using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Queries;

public record StreakHistoryPoint(DateOnly Date, int Streak);

public record StreakHistoryResponse(IReadOnlyList<StreakHistoryPoint> Points);

public record GetStreakHistoryQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo) : IRequest<Result<StreakHistoryResponse>>;

/// <summary>
/// Recomputes the user's day-by-day streak across a date range from habit completions and streak
/// freezes: a day maintains the streak when it has at least one completion or a freeze, otherwise the
/// streak resets. Seeds from a lookback before the range so the streak entering the window is correct.
/// Pro-gated like the streak/gamification surfaces.
/// </summary>
public class GetStreakHistoryQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IFeatureFlagService featureFlagService) : IRequestHandler<GetStreakHistoryQuery, Result<StreakHistoryResponse>>
{
    public async Task<Result<StreakHistoryResponse>> Handle(GetStreakHistoryQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<StreakHistoryResponse>(ErrorMessages.UserNotFound);

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
        var unlocked = user.HasProAccess || enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier);
        if (!unlocked)
            return Result.PayGateFailure<StreakHistoryResponse>("Streak insights are a Pro feature. Upgrade to unlock!");

        var windowStart = request.DateFrom.AddDays(-AppConstants.MaxStreakLookbackDays);

        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && !h.IsBadHabit,
            q => q.Include(h => h.Logs.Where(l => l.Date >= windowStart && l.Date <= request.DateTo)),
            cancellationToken);

        var activeDates = habits
            .SelectMany(h => h.Logs)
            .Where(l => l.Value > 0)
            .Select(l => l.Date)
            .ToHashSet();

        var freezes = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= windowStart && sf.UsedOnDate <= request.DateTo,
            cancellationToken);
        var frozenDates = freezes.Select(sf => sf.UsedOnDate).ToHashSet();

        var points = new List<StreakHistoryPoint>();
        var streak = 0;
        for (var date = windowStart; date <= request.DateTo; date = date.AddDays(1))
        {
            var maintained = activeDates.Contains(date) || frozenDates.Contains(date);
            streak = maintained ? streak + 1 : 0;
            if (date >= request.DateFrom)
                points.Add(new StreakHistoryPoint(date, streak));
        }

        return Result.Success(new StreakHistoryResponse(points));
    }
}
