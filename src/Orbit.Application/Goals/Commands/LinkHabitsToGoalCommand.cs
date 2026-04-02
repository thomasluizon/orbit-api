using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record LinkHabitsToGoalCommand(
    Guid UserId,
    Guid GoalId,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result>;

public class LinkHabitsToGoalCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<LinkHabitsToGoalCommand, Result>
{
    public async Task<Result> Handle(LinkHabitsToGoalCommand request, CancellationToken cancellationToken)
    {
        if (request.HabitIds.Count > AppConstants.MaxHabitsPerGoal)
            return Result.Failure($"A goal can have at most {AppConstants.MaxHabitsPerGoal} linked habits.");

        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            q => q.Include(g => g.Habits),
            cancellationToken);

        if (goal is null)
            return Result.Failure(ErrorMessages.GoalNotFound, ErrorCodes.GoalNotFound);

        var habits = await habitRepository.FindTrackedAsync(
            h => request.HabitIds.Contains(h.Id) && h.UserId == request.UserId,
            cancellationToken);

        // Clear existing and reassign
        foreach (var existing in goal.Habits.ToList())
            goal.RemoveHabit(existing);

        foreach (var habit in habits)
            goal.AddHabit(habit);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
