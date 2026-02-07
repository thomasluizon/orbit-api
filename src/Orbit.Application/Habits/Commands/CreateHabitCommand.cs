using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record CreateHabitCommand(
    Guid UserId,
    string Title,
    string? Description,
    HabitFrequency Frequency,
    HabitType Type,
    string? Unit,
    decimal? TargetValue) : IRequest<Result<Guid>>;

public class CreateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)
    {
        var habitResult = Habit.Create(
            request.UserId,
            request.Title,
            request.Frequency,
            request.Type,
            request.Description,
            request.Unit,
            request.TargetValue);

        if (habitResult.IsFailure)
            return Result.Failure<Guid>(habitResult.Error);

        await habitRepository.AddAsync(habitResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(habitResult.Value.Id);
    }
}
