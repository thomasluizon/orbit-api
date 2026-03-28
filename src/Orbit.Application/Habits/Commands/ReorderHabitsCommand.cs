using MediatR;
using Orbit.Application.Common;
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
        var ids = request.Positions.Select(p => p.HabitId).ToHashSet();

        // Load all habits in a single query instead of one per position update
        var habits = await habitRepository.FindTrackedAsync(
            h => ids.Contains(h.Id) && h.UserId == request.UserId,
            cancellationToken);

        var habitMap = habits.ToDictionary(h => h.Id);

        foreach (var update in request.Positions)
        {
            if (!habitMap.TryGetValue(update.HabitId, out var habit))
                return Result.Failure(ErrorMessages.HabitNotFound);

            habit.SetPosition(update.Position);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
