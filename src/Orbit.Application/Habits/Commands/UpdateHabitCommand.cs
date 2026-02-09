using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record UpdateHabitCommand(
    Guid UserId,
    Guid HabitId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<System.DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    DateOnly? DueDate = null) : IRequest<Result>;

public class UpdateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateHabitCommand, Result>
{
    public async Task<Result> Handle(UpdateHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure("Habit not found.");

        var result = habit.Update(
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Days,
            request.IsBadHabit,
            request.DueDate);

        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
