using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Commands;

public record UpdateChecklistCommand(
    Guid UserId,
    Guid HabitId,
    IReadOnlyList<ChecklistItem> ChecklistItems) : IRequest<Result>;

public class UpdateChecklistCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateChecklistCommand, Result>
{
    public async Task<Result> Handle(UpdateChecklistCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        habit.UpdateChecklist(request.ChecklistItems);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
