using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record HabitPositionUpdate(Guid HabitId, int Position);

public record ReorderHabitsCommand(
    Guid UserId,
    IReadOnlyList<HabitPositionUpdate> Positions) : IRequest<Result>;

public class ReorderHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<ReorderHabitsCommand, Result>
{
    public async Task<Result> Handle(ReorderHabitsCommand request, CancellationToken cancellationToken)
    {
        foreach (var update in request.Positions)
        {
            var habit = await habitRepository.FindOneTrackedAsync(
                h => h.Id == update.HabitId && h.UserId == request.UserId,
                cancellationToken: cancellationToken);

            if (habit is null)
                return Result.Failure($"Habit '{update.HabitId}' not found.");

            habit.SetPosition(update.Position);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
