using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
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
    IUserStreakService userStreakService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<BulkDeleteHabitsCommand, Result<BulkDeleteResult>>
{
    public async Task<Result<BulkDeleteResult>> Handle(BulkDeleteHabitsCommand request, CancellationToken cancellationToken)
    {
        var results = new List<BulkDeleteItemResult>();

        await HabitCeilingLock.ExecuteAsync(unitOfWork, request.UserId, async ct =>
        {
            var userHabits = await habitRepository.FindTrackedAsync(
                h => h.UserId == request.UserId,
                query => query.Include(h => h.Goals),
                ct);
            var habitDict = userHabits.ToDictionary(h => h.Id);
            var childrenByParentId = userHabits.ToLookup(h => h.ParentHabitId);
            var deletedIds = new HashSet<Guid>();
            var deletedAtUtc = DateTime.UtcNow;

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

                foreach (var deleted in HabitHierarchy.SoftDeleteSubtree(
                    habit, childrenByParentId, deletedIds, deletedAtUtc))
                {
                    results.Add(new BulkDeleteItemResult(
                        Index: i,
                        Status: BulkItemStatus.Success,
                        HabitId: deleted.Id));
                }
            }

            await unitOfWork.SaveChangesAsync(ct);
            if (results.Any(r => r.Status == BulkItemStatus.Success))
            {
                await ConcurrencyRetry.SaveWithRetryAsync(
                    unitOfWork,
                    c => userStreakService.RecalculateAsync(request.UserId, cancellationToken: c),
                    ct);
            }
        }, cancellationToken);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success(new BulkDeleteResult(results));
    }
}
