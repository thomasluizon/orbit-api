using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkLogHabitsCommand(
    Guid UserId,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result<BulkLogResult>>;

public record BulkLogResult(IReadOnlyList<BulkLogItemResult> Results);

public record BulkLogItemResult(
    int Index,
    BulkItemStatus Status,
    Guid HabitId,
    Guid? LogId = null,
    string? Error = null);

public class BulkLogHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<BulkLogHabitsCommand, Result<BulkLogResult>>
{
    public async Task<Result<BulkLogResult>> Handle(BulkLogHabitsCommand request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var results = new List<BulkLogItemResult>();

        for (int i = 0; i < request.HabitIds.Count; i++)
        {
            var habitId = request.HabitIds[i];

            try
            {
                var habit = await habitRepository.FindOneTrackedAsync(
                    h => h.Id == habitId,
                    q => q.Include(h => h.Logs),
                    cancellationToken);

                if (habit is null)
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: ErrorMessages.HabitNotFound));
                    continue;
                }

                if (habit.UserId != request.UserId)
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: ErrorMessages.HabitNotOwned));
                    continue;
                }

                // Skip if already logged today (no toggle -- just skip)
                var alreadyLogged = habit.Logs.Any(l => l.Date == today);
                if (alreadyLogged)
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Success,
                        HabitId: habitId));
                    continue;
                }

                var logResult = habit.Log(today);
                if (logResult.IsFailure)
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: logResult.Error));
                    continue;
                }

                await habitLogRepository.AddAsync(logResult.Value, cancellationToken);

                // Auto-complete parent when all children are done (recursive up the tree)
                await TryAutoCompleteParent(habit, today, cancellationToken);

                results.Add(new BulkLogItemResult(
                    Index: i,
                    Status: BulkItemStatus.Success,
                    HabitId: habitId,
                    LogId: logResult.Value.Id));
            }
            catch (Exception ex)
            {
                results.Add(new BulkLogItemResult(
                    Index: i,
                    Status: BulkItemStatus.Failed,
                    HabitId: habitId,
                    Error: ex.Message));
            }
        }

        // Save all successful logs once
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Gamification: process each successfully logged habit
        foreach (var item in results.Where(r => r.Status == BulkItemStatus.Success && r.LogId is not null))
        {
            try
            {
                await gamificationService.ProcessHabitLogged(request.UserId, item.HabitId, cancellationToken);
            }
            catch { /* gamification failure should not block bulk logging */ }
        }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(new BulkLogResult(results));
    }

    private async Task TryAutoCompleteParent(Habit child, DateOnly today, CancellationToken ct)
    {
        if (child.ParentHabitId is null) return;

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == child.ParentHabitId.Value,
            q => q.Include(h => h.Logs)
                  .Include(h => h.Children).ThenInclude(c => c.Logs),
            ct);

        if (parent is null || parent.IsCompleted) return;

        // Only auto-log if the parent is actually due today (or overdue)
        if (parent.DueDate > today) return;

        // Check if ALL children are done for today (logged today or permanently completed)
        if (!parent.Children.Any()) return;

        var allChildrenDone = parent.Children.All(c =>
            c.IsCompleted || c.Logs.Any(l => l.Date == today));
        if (!allChildrenDone) return;

        // Auto-log the parent
        var alreadyLogged = parent.Logs.Any(l => l.Date == today);
        if (!alreadyLogged)
        {
            var logResult = parent.Log(today);
            if (logResult.IsSuccess)
                await habitLogRepository.AddAsync(logResult.Value, ct);
        }

        // Recurse up the tree
        await TryAutoCompleteParent(parent, today, ct);
    }
}
