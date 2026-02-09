using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkDeleteHabitsCommand(
    Guid UserId,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result<BulkDeleteResult>>;

public record BulkDeleteResult(IReadOnlyList<BulkDeleteItemResult> Results);

public record BulkDeleteItemResult(
    int Index,
    BulkItemStatus Status,
    Guid HabitId,
    string? Error = null);

public class BulkDeleteHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<BulkDeleteHabitsCommand, Result<BulkDeleteResult>>
{
    public async Task<Result<BulkDeleteResult>> Handle(BulkDeleteHabitsCommand request, CancellationToken cancellationToken)
    {
        var results = new List<BulkDeleteItemResult>();

        for (int i = 0; i < request.HabitIds.Count; i++)
        {
            var habitId = request.HabitIds[i];

            try
            {
                var habit = await habitRepository.GetByIdAsync(habitId, cancellationToken);

                if (habit is null)
                {
                    results.Add(new BulkDeleteItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Habit not found"));
                    continue;
                }

                if (habit.UserId != request.UserId)
                {
                    results.Add(new BulkDeleteItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Habit does not belong to user"));
                    continue;
                }

                habitRepository.Remove(habit);

                results.Add(new BulkDeleteItemResult(
                    Index: i,
                    Status: BulkItemStatus.Success,
                    HabitId: habitId));
            }
            catch (Exception ex)
            {
                results.Add(new BulkDeleteItemResult(
                    Index: i,
                    Status: BulkItemStatus.Failed,
                    HabitId: habitId,
                    Error: ex.Message));
            }
        }

        // Save all successful deletions once
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new BulkDeleteResult(results));
    }
}
