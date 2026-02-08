using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record RemoveSubHabitCommand(
    Guid UserId,
    Guid HabitId,
    Guid SubHabitId) : IRequest<Result>;

public class RemoveSubHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<RemoveSubHabitCommand, Result>
{
    public async Task<Result> Handle(RemoveSubHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.SubHabits),
            cancellationToken);

        if (habit is null)
            return Result.Failure("Habit not found.");

        if (habit.UserId != request.UserId)
            return Result.Failure("Habit does not belong to this user.");

        var result = habit.RemoveSubHabit(request.SubHabitId);

        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
