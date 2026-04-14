using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Queries;

public record GoalReviewResponse(string Review, bool FromCache);

public record GetGoalReviewQuery(Guid UserId, string Language) : IRequest<Result<GoalReviewResponse>>;

public class GetGoalReviewQueryHandler(
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IGoalReviewService goalReviewService,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<GetGoalReviewQuery, Result<GoalReviewResponse>>
{
    public async Task<Result<GoalReviewResponse>> Handle(
        GetGoalReviewQuery request,
        CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<GoalReviewResponse>();

        var cacheKey = $"goal-review:{request.UserId}:{request.Language}";

        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return Result.Success(new GoalReviewResponse(cached, FromCache: true));

        var goals = await goalRepository.FindAsync(
            g => g.UserId == request.UserId && g.Status == GoalStatus.Active,
            q => q.Include(g => g.ProgressLogs)
                  .Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken);

        var goalList = goals.ToList();

        if (goalList.Count == 0)
            return Result.Failure<GoalReviewResponse>("No active goals found.");

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var goalsContext = BuildGoalsContext(goalList, userToday);

        var result = await goalReviewService.GenerateReviewAsync(
            goalsContext,
            request.Language,
            cancellationToken);

        if (result.IsFailure)
            return Result.Failure<GoalReviewResponse>(result.Error);

        cache.Set(cacheKey, result.Value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });

        return Result.Success(new GoalReviewResponse(result.Value, FromCache: false));
    }

    private static string BuildGoalsContext(List<Goal> goals, DateOnly userToday)
    {
        var lines = new List<string>();

        foreach (var goal in goals)
        {
            var metrics = GoalMetricsCalculator.Calculate(goal, userToday);
            lines.Add($"Goal: \"{goal.Title}\" | {goal.CurrentValue}/{goal.TargetValue} {goal.Unit} ({metrics.ProgressPercentage}%)");
            lines.Add($"  Status: {metrics.TrackingStatus} | Velocity: {metrics.VelocityPerDay} {goal.Unit}/day");

            if (metrics.ProjectedCompletionDate.HasValue)
                lines.Add($"  Projected completion: {metrics.ProjectedCompletionDate:yyyy-MM-dd}");

            if (goal.Deadline.HasValue)
                lines.Add($"  Deadline: {goal.Deadline:yyyy-MM-dd} ({metrics.DaysToDeadline} days remaining)");

            foreach (var h in metrics.HabitAdherence)
                lines.Add($"  Linked habit: \"{h.HabitTitle}\" | Weekly: {h.WeeklyCompletionRate}% | Streak: {h.CurrentStreak}d");
        }

        return string.Join("\n", lines);
    }
}
