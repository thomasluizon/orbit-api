using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record MoveHabitParentCommand(
    Guid UserId,
    Guid HabitId,
    Guid? ParentId) : IRequest<Result>;

public class MoveHabitParentCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<MoveHabitParentCommand, Result>
{
    public async Task<Result> Handle(MoveHabitParentCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Children),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        // Promote to top-level
        if (request.ParentId is null)
        {
            habit.SetParentHabitId(null);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        // Cannot be its own parent
        if (request.ParentId == request.HabitId)
            return Result.Failure("A habit cannot be its own parent.");

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.ParentId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (parent is null)
            return Result.Failure(ErrorMessages.TargetParentNotFound, ErrorCodes.TargetParentNotFound);

        // Prevent circular references: walk up from the target parent to ensure
        // we don't encounter the habit being moved
        if (await WouldCreateCycle(request.HabitId, request.ParentId.Value, request.UserId, cancellationToken))
            return Result.Failure("Cannot move a habit under its own descendant.");

        habit.SetParentHabitId(request.ParentId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<bool> WouldCreateCycle(Guid habitId, Guid targetParentId, Guid userId, CancellationToken cancellationToken)
    {
        // Load all user habits once and walk in memory instead of N+1 queries
        var allHabits = await habitRepository.FindAsync(h => h.UserId == userId, cancellationToken);
        var habitDict = allHabits.ToDictionary(h => h.Id);

        var currentId = targetParentId;
        while (habitDict.TryGetValue(currentId, out var current))
        {
            if (current.ParentHabitId is null) return false;
            if (current.ParentHabitId == habitId) return true;
            currentId = current.ParentHabitId.Value;
        }
        return false;
    }
}
