using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
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
/// Day-by-day streak series for the requested range, computed with the same schedule-aware engine that
/// produces <see cref="User.CurrentStreak"/>: only scheduled (expected) days can break or extend a streak,
/// off-days are skipped rather than reset, and freezes bridge missed days. The value on DateTo equals the
/// user's live current streak. Seeds from a lookback before the range so the streak entering the window is
/// correct. Pro-gated like the streak/gamification surfaces.
/// </summary>
public class GetStreakHistoryQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
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

        var seedFrom = request.DateFrom.AddDays(-AppConstants.MaxStreakLookbackDays);

        var allHabits = await habitRepository.FindAsync(h => h.UserId == request.UserId, cancellationToken);
        var streakEligibleHabitIds = allHabits
            .Where(h => !h.IsDeleted && !h.IsBadHabit)
            .Select(h => h.Id)
            .ToHashSet();

        var completionDates = streakEligibleHabitIds.Count == 0
            ? new HashSet<DateOnly>()
            : (await habitLogRepository.FindAsync(
                l => streakEligibleHabitIds.Contains(l.HabitId) && l.Value > 0
                    && l.Date >= seedFrom && l.Date <= request.DateTo,
                cancellationToken))
                .Select(log => log.Date)
                .ToHashSet();

        var freezeDates = (await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= seedFrom && sf.UsedOnDate <= request.DateTo,
            cancellationToken))
            .Select(freeze => freeze.UsedOnDate)
            .ToHashSet();

        var userTimeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, userId: user.Id);
        var expectedDates = HabitScheduleService.GetUnionScheduledDatesForStreak(
            allHabits, seedFrom, request.DateTo, userTimeZone);

        var series = HabitScheduleService.BuildStreakSeries(
            expectedDates, completionDates, freezeDates, seedFrom, request.DateFrom, request.DateTo);

        var points = series
            .Select(point => new StreakHistoryPoint(point.Date, point.Streak))
            .ToList();

        return Result.Success(new StreakHistoryResponse(points));
    }
}
