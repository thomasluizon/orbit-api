using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Queries;

public record GoalProgressEntryDto(
    decimal Value,
    decimal PreviousValue,
    string? Note,
    DateTime CreatedAtUtc);

public record GoalDetailDto(
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
    IReadOnlyList<GoalProgressEntryDto> ProgressHistory,
    List<LinkedHabitDto> LinkedHabits);

public record GetGoalByIdQuery(
    Guid UserId,
    Guid GoalId) : IRequest<Result<GoalDetailDto>>;

public class GetGoalByIdQueryHandler(
    IGenericRepository<Goal> goalRepository) : IRequestHandler<GetGoalByIdQuery, Result<GoalDetailDto>>
{
    public async Task<Result<GoalDetailDto>> Handle(GetGoalByIdQuery request, CancellationToken cancellationToken)
    {
        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            includes: q => q.Include(g => g.ProgressLogs).Include(g => g.Habits),
            cancellationToken: cancellationToken);

        if (goal is null)
            return Result.Failure<GoalDetailDto>(ErrorMessages.GoalNotFound);

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

        return Result.Success(new GoalDetailDto(
            goal.Id, goal.Title, goal.Description, goal.TargetValue, goal.CurrentValue,
            goal.Unit, goal.Status, goal.Deadline, goal.Position, goal.CreatedAtUtc,
            goal.CompletedAtUtc, progressPercentage, progressHistory, linkedHabits));
    }
}
