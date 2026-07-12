using System.Linq.Expressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Queries;

public record LinkedHabitDto(Guid Id, string Title);

public record GoalDto(
    Guid Id,
    string Title,
    string? Description,
    decimal TargetValue,
    decimal CurrentValue,
    string Unit,
    GoalStatus Status,
    GoalType Type,
    DateOnly? Deadline,
    int Position,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    decimal ProgressPercentage,
    List<LinkedHabitDto> LinkedHabits,
    string? TrackingStatus);

public record GetGoalsQuery(
    Guid UserId,
    GoalStatus? StatusFilter = null,
    int Page = 1,
    int PageSize = 50) : IRequest<Result<PaginatedResponse<GoalDto>>>;

public class GetGoalsQueryHandler(
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IStreakGoalReadSyncer streakGoalReadSyncer) : IRequestHandler<GetGoalsQuery, Result<PaginatedResponse<GoalDto>>>
{
    public async Task<Result<PaginatedResponse<GoalDto>>> Handle(GetGoalsQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<PaginatedResponse<GoalDto>>();

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var freshStreakValues = await streakGoalReadSyncer.ComputeFreshValuesAsync(request.UserId, userToday, cancellationToken);

        var statusFilter = request.StatusFilter;
        Expression<Func<Goal, bool>> predicate = statusFilter.HasValue
            ? g => g.UserId == request.UserId && g.Status == statusFilter.Value
            : g => g.UserId == request.UserId;

        var (pageGoals, totalCount) = await goalRepository.FindPagedAsync(
            predicate,
            q => q.OrderBy(g => g.Position).ThenBy(g => g.CreatedAtUtc),
            request.Page,
            request.PageSize,
            q => q.Include(g => g.Habits),
            cancellationToken);

        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);

        var items = pageGoals
            .Select(g => MapToDto(g, userToday, ResolveCurrentValue(g, freshStreakValues)))
            .ToList();

        return Result.Success(new PaginatedResponse<GoalDto>(items, request.Page, request.PageSize, totalCount, totalPages));
    }

    private static decimal ResolveCurrentValue(Goal goal, IReadOnlyDictionary<Guid, int> freshStreakValues) =>
        freshStreakValues.TryGetValue(goal.Id, out var fresh) ? fresh : goal.CurrentValue;

    private static GoalDto MapToDto(Goal g, DateOnly userToday, decimal currentValue) => new(
        g.Id, g.Title, g.Description, g.TargetValue, currentValue,
        g.Unit, g.Status, g.Type, g.Deadline, g.Position, g.CreatedAtUtc, g.CompletedAtUtc,
        g.TargetValue > 0 ? Math.Min(100, Math.Round(currentValue / g.TargetValue * 100, 1)) : 0,
        g.Habits.Select(h => new LinkedHabitDto(h.Id, h.Title)).ToList(),
        ComputeSimpleTrackingStatus(g, userToday, currentValue));

    private static string? ComputeSimpleTrackingStatus(Goal goal, DateOnly userToday, decimal currentValue)
    {
        if (goal.Status == GoalStatus.Completed) return "completed";
        if (goal.Status == GoalStatus.Abandoned) return null;
        if (!goal.Deadline.HasValue) return "no_deadline";

        var daysToDeadline = goal.Deadline.Value.DayNumber - userToday.DayNumber;
        if (daysToDeadline < 0) return "behind";

        var progress = goal.TargetValue > 0
            ? currentValue / goal.TargetValue * 100
            : 0;
        if (daysToDeadline <= 7 && progress < 50) return "at_risk";

        return "on_track";
    }
}
