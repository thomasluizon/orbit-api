using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
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
    IUnitOfWork unitOfWork) : IRequestHandler<LinkGoalsToHabitCommand, Result>
{
    public async Task<Result> Handle(LinkGoalsToHabitCommand request, CancellationToken cancellationToken)
    {
        if (request.GoalIds.Count > AppConstants.MaxGoalsPerHabit)
            return Result.Failure($"A habit can have at most {AppConstants.MaxGoalsPerHabit} linked goals.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Goals),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var goals = await goalRepository.FindTrackedAsync(
            g => request.GoalIds.Contains(g.Id) && g.UserId == request.UserId,
            cancellationToken);

        // Clear existing and reassign
        foreach (var existing in habit.Goals.ToList())
            habit.RemoveGoal(existing);

        foreach (var goal in goals)
            habit.AddGoal(goal);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
