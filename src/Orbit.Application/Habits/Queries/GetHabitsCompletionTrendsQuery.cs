using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record HabitsCompletionTrendPoint(DateOnly Date, int CompletedCount, decimal CompletionRate);

public record HabitsCompletionTrendsResponse(int ActiveHabitCount, IReadOnlyList<HabitsCompletionTrendPoint> Points);

public record GetHabitsCompletionTrendsQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo) : IRequest<Result<HabitsCompletionTrendsResponse>>;

/// <summary>
/// Returns the user's daily habit-completion trend across a date range: per day the number of
/// top-level good habits completed and that count as a rate of the user's active top-level habits.
/// Loads habits with their in-range logs in one batched query (no N+1) and aggregates in memory.
/// Pro-gated like the insights surfaces.
/// </summary>
public sealed record HabitCompletionDates(Guid Id, IReadOnlyList<DateOnly> Dates);

public class GetHabitsCompletionTrendsQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IFeatureFlagService featureFlagService) : IRequestHandler<GetHabitsCompletionTrendsQuery, Result<HabitsCompletionTrendsResponse>>
{
    public async Task<Result<HabitsCompletionTrendsResponse>> Handle(GetHabitsCompletionTrendsQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<HabitsCompletionTrendsResponse>(ErrorMessages.UserNotFound);

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
        var unlocked = GamificationAccess.IsUnlocked(user, enabledFlags);
        if (!unlocked)
            return Result.PayGateFailure<HabitsCompletionTrendsResponse>("Insights are a Pro feature. Upgrade to unlock!");

        var habits = await habitRepository.ProjectAsync(
            h => h.UserId == request.UserId && !h.IsBadHabit && h.ParentHabitId == null,
            query => query.Select(h => new HabitCompletionDates(h.Id,
                h.Logs.Where(l => l.Date >= request.DateFrom && l.Date <= request.DateTo && l.Value > 0)
                    .Select(l => l.Date).ToList())).AsSplitQuery(), cancellationToken);
        var activeHabitCount = habits.Count;
        var completionsByDate = habits.SelectMany(h => h.Dates).GroupBy(date => date)
            .ToDictionary(group => group.Key, group => group.Count());

        var points = new List<HabitsCompletionTrendPoint>();
        for (var date = request.DateFrom; date <= request.DateTo; date = date.AddDays(1))
        {
            var completed = completionsByDate.GetValueOrDefault(date, 0);
            var rate = activeHabitCount > 0
                ? Math.Round(Math.Min(100m, (decimal)completed / activeHabitCount * 100), 1)
                : 0m;
            points.Add(new HabitsCompletionTrendPoint(date, completed, rate));
        }

        return Result.Success(new HabitsCompletionTrendsResponse(activeHabitCount, points));
    }
}
