using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
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
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkDeleteHabitsCommandHandler> logger) : IRequestHandler<BulkDeleteHabitsCommand, Result<BulkDeleteResult>>
{
    public async Task<Result<BulkDeleteResult>> Handle(BulkDeleteHabitsCommand request, CancellationToken cancellationToken)
    {
        var results = new List<BulkDeleteItemResult>();

        // Batch-load all requested habits in a single query instead of N+1
        var habits = await habitRepository.FindTrackedAsync(
            h => request.HabitIds.Contains(h.Id) && h.UserId == request.UserId,
            cancellationToken);
        var habitDict = habits.ToDictionary(h => h.Id);

        await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            for (int i = 0; i < request.HabitIds.Count; i++)
            {
                var habitId = request.HabitIds[i];

                if (!habitDict.TryGetValue(habitId, out var habit))
                {
                    results.Add(new BulkDeleteItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Habit not found or not owned by user."));
                    continue;
                }

                habitRepository.Remove(habit);
                results.Add(new BulkDeleteItemResult(
                    Index: i,
                    Status: BulkItemStatus.Success,
                    HabitId: habitId));
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            throw;
        }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(new BulkDeleteResult(results));
    }
}
