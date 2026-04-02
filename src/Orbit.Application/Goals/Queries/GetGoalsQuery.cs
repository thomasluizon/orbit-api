using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
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
    IUserDateService userDateService) : IRequestHandler<GetGoalsQuery, Result<PaginatedResponse<GoalDto>>>
{
    public async Task<Result<PaginatedResponse<GoalDto>>> Handle(GetGoalsQuery request, CancellationToken cancellationToken)
    {
        var allGoals = await goalRepository.FindAsync(
            g => g.UserId == request.UserId,
            q => q.Include(g => g.Habits),
            cancellationToken);

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        IEnumerable<Goal> filtered = allGoals;

        if (request.StatusFilter.HasValue)
            filtered = filtered.Where(g => g.Status == request.StatusFilter.Value);

        var ordered = filtered
            .OrderBy(g => g.Position)
            .ThenBy(g => g.CreatedAtUtc)
            .ToList();

        var totalCount = ordered.Count;
        var totalPages = (int)Math.Ceiling((double)totalCount / request.PageSize);

        var items = ordered
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(g => MapToDto(g, userToday))
            .ToList();

        return Result.Success(new PaginatedResponse<GoalDto>(items, request.Page, request.PageSize, totalCount, totalPages));
    }

    private static GoalDto MapToDto(Goal g, DateOnly userToday) => new(
        g.Id, g.Title, g.Description, g.TargetValue, g.CurrentValue,
        g.Unit, g.Status, g.Deadline, g.Position, g.CreatedAtUtc, g.CompletedAtUtc,
        g.TargetValue > 0 ? Math.Min(100, Math.Round(g.CurrentValue / g.TargetValue * 100, 1)) : 0,
        g.Habits.Select(h => new LinkedHabitDto(h.Id, h.Title)).ToList(),
        ComputeSimpleTrackingStatus(g, userToday));

    private static string? ComputeSimpleTrackingStatus(Goal goal, DateOnly userToday)
    {
        if (goal.Status == GoalStatus.Completed) return "completed";
        if (goal.Status == GoalStatus.Abandoned) return null;
        if (!goal.Deadline.HasValue) return "no_deadline";

        var daysToDeadline = goal.Deadline.Value.DayNumber - userToday.DayNumber;
        if (daysToDeadline < 0) return "behind";

        var progress = goal.TargetValue > 0
            ? goal.CurrentValue / goal.TargetValue * 100
            : 0;
        if (daysToDeadline <= 7 && progress < 50) return "at_risk";

        return "on_track";
    }
}
