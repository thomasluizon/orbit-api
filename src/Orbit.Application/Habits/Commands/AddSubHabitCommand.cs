using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record AddSubHabitCommand(
    Guid UserId,
    Guid HabitId,
    string Title,
    int SortOrder = 0) : IRequest<Result<Guid>>;

public class AddSubHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<AddSubHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddSubHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.SubHabits),
            cancellationToken);

        if (habit is null)
            return Result.Failure<Guid>("Habit not found.");

        if (habit.UserId != request.UserId)
            return Result.Failure<Guid>("Habit does not belong to this user.");

        var result = habit.AddSubHabit(request.Title, request.SortOrder);

        if (result.IsFailure)
            return Result.Failure<Guid>(result.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(result.Value.Id);
    }
}
