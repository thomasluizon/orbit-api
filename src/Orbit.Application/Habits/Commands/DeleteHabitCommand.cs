using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record DeleteHabitCommand(Guid UserId, Guid HabitId) : IRequest<Result>;

public class DeleteHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DeleteHabitCommand, Result>
{
    public async Task<Result> Handle(DeleteHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.GetByIdAsync(request.HabitId, cancellationToken);

        if (habit is null)
            return Result.Failure("Habit not found.");

        if (habit.UserId != request.UserId)
            return Result.Failure("You don't have permission to delete this habit.");

        habitRepository.Remove(habit);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
