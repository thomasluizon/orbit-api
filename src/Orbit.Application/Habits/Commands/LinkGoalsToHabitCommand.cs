using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record LinkGoalsToHabitCommand(
    Guid UserId,
    Guid HabitId,
    IReadOnlyList<Guid> GoalIds) : IRequest<Result>;

public class LinkGoalsToHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Goal> goalRepository,
    IGoalCompletionService goalCompletionService,
    IUserDateService userDateService) : IRequestHandler<LinkGoalsToHabitCommand, Result>
{
    public async Task<Result> Handle(LinkGoalsToHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Goals),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var affectedGoalIds = habit.Goals.Select(g => g.Id).Concat(request.GoalIds).ToHashSet();
        var goals = await goalRepository.FindTrackedAsync(
            g => affectedGoalIds.Contains(g.Id) && g.UserId == request.UserId,
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken);

        var requestedGoals = goals.Where(g => request.GoalIds.Contains(g.Id)).ToList();
        var goalsResolved = OwnershipValidation.AllResolved(request.GoalIds, requestedGoals, g => g.Id, ErrorMessages.GoalNotFound);
        if (goalsResolved.IsFailure)
            return goalsResolved;

        foreach (var existing in habit.Goals.ToList())
            habit.RemoveGoal(existing);

        foreach (var goal in requestedGoals)
            habit.AddGoal(goal);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        await goalCompletionService.SyncDerivedGoalsAsync(
            request.UserId,
            affectedGoalIds,
            today,
            cancellationToken: cancellationToken);

        return Result.Success();
    }
}
