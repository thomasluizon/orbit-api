using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Goals.Queries;

public record GoalDetailWithMetricsResponse(
    GoalDetailDto Goal,
    GoalMetrics Metrics);

public record GetGoalDetailQuery(
    Guid UserId,
    Guid GoalId) : IRequest<Result<GoalDetailWithMetricsResponse>>;

public class GetGoalDetailQueryHandler(
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IRequestHandler<GetGoalDetailQuery, Result<GoalDetailWithMetricsResponse>>
{
    public async Task<Result<GoalDetailWithMetricsResponse>> Handle(GetGoalDetailQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<GoalDetailWithMetricsResponse>();

        // Superset includes: ProgressLogs for detail + Habits.Logs for metrics
        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            includes: q => q.Include(g => g.ProgressLogs)
                            .Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken: cancellationToken);

        if (goal is null)
            return Result.Failure<GoalDetailWithMetricsResponse>(ErrorMessages.GoalNotFound, ErrorCodes.GoalNotFound);

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        if (GoalStreakSyncService.SyncCurrentStreakIfNeeded(goal, userToday))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Build detail DTO (same as GetGoalByIdQueryHandler)
        var progressPercentage = goal.TargetValue > 0
            ? Math.Min(100, Math.Round(goal.CurrentValue / goal.TargetValue * 100, 1))
            : 0;

        var progressHistory = goal.ProgressLogs
            .OrderByDescending(l => l.CreatedAtUtc)
            .Select(l => new GoalProgressEntryDto(l.Value, l.PreviousValue, l.Note, l.CreatedAtUtc))
            .ToList();

        var linkedHabits = goal.Habits
            .Select(h => new LinkedHabitDto(h.Id, h.Title))
            .ToList();

        var detail = new GoalDetailDto(
            goal.Id, goal.Title, goal.Description, goal.TargetValue, goal.CurrentValue,
            goal.Unit, goal.Status, goal.Type, goal.Deadline, goal.Position, goal.CreatedAtUtc,
            goal.CompletedAtUtc, progressPercentage, progressHistory, linkedHabits);

        // Build metrics
        var goalMetrics = GoalMetricsCalculator.Calculate(goal, userToday);

        return Result.Success(new GoalDetailWithMetricsResponse(detail, goalMetrics));
    }
}
