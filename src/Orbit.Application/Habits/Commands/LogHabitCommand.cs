using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record LogHabitCommand(
    Guid UserId,
    Guid HabitId,
    DateOnly Date,
    decimal? Value) : IRequest<Result<Guid>>;

public class LogHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<LogHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(LogHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.GetByIdAsync(request.HabitId, cancellationToken);

        if (habit is null)
            return Result.Failure<Guid>("Habit not found.");

        if (habit.UserId != request.UserId)
            return Result.Failure<Guid>("Habit does not belong to this user.");

        var logResult = habit.Log(request.Date, request.Value);

        if (logResult.IsFailure)
            return Result.Failure<Guid>(logResult.Error);

        // No need to call Update - EF tracks the new HabitLog automatically
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(logResult.Value.Id);
    }
}
