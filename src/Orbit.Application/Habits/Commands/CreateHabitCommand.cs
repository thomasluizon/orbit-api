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
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    HabitType Type,
    string? Unit,
    decimal? TargetValue,
    IReadOnlyList<System.DayOfWeek>? Days = null,
    bool IsNegative = false,
    IReadOnlyList<string>? SubHabits = null,
    DateOnly? DueDate = null) : IRequest<Result<Guid>>;

public class CreateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)
    {
        var habitResult = Habit.Create(
            request.UserId,
            request.Title,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Type,
            request.Description,
            request.Unit,
            request.TargetValue,
            request.Days,
            request.IsNegative,
            request.DueDate);

        if (habitResult.IsFailure)
            return Result.Failure<Guid>(habitResult.Error);

        var habit = habitResult.Value;

        if (request.SubHabits is { Count: > 0 })
        {
            foreach (var subTitle in request.SubHabits)
            {
                var childResult = Habit.Create(
                    request.UserId,
                    subTitle,
                    request.FrequencyUnit,
                    request.FrequencyQuantity,
                    HabitType.Boolean,
                    dueDate: request.DueDate,
                    parentHabitId: habit.Id);

                if (childResult.IsFailure)
                    return Result.Failure<Guid>(childResult.Error);

                await habitRepository.AddAsync(childResult.Value, cancellationToken);
            }
        }

        await habitRepository.AddAsync(habit, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(habit.Id);
    }
}
