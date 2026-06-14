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

        var habits = await habitRepository.FindTrackedAsync(
            h => ids.Contains(h.Id) && h.UserId == request.UserId,
            cancellationToken);

        var habitMap = habits.ToDictionary(h => h.Id);

        var orderedHabitIds = request.Positions
            .OrderBy(p => p.Position)
            .Select(p => p.HabitId)
            .ToList();

        var normalizedPosition = 0;
        foreach (var habitId in orderedHabitIds)
        {
            if (!habitMap.TryGetValue(habitId, out var habit))
                return Result.Failure(ErrorMessages.HabitNotFound);

            habit.SetPosition(normalizedPosition++);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
