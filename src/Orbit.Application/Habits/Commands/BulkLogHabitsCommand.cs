using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkLogItem(Guid HabitId, DateOnly? Date = null);

public record BulkLogHabitsCommand(
    Guid UserId,
    IReadOnlyList<BulkLogItem> Items) : IRequest<Result<BulkLogResult>>;

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
    IMemoryCache cache,
    ILogger<BulkLogHabitsCommandHandler> logger) : IRequestHandler<BulkLogHabitsCommand, Result<BulkLogResult>>
{
    public async Task<Result<BulkLogResult>> Handle(BulkLogHabitsCommand request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var results = new List<BulkLogItemResult>();

        // Batch-load all requested habits in a single query instead of one per item (N+1)
        var habitIds = request.Items.Select(i => i.HabitId).ToHashSet();
        var habits = await habitRepository.FindTrackedAsync(
            h => habitIds.Contains(h.Id) && h.UserId == request.UserId,
            q => q.Include(h => h.Logs),
            cancellationToken);
        var habitMap = habits.ToDictionary(h => h.Id);

        for (int i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            var habitId = item.HabitId;
            var targetDate = item.Date ?? today;

            try
            {
                if (targetDate > today)
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Cannot log a future date."));
                    continue;
                }

                if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Cannot log a date beyond the overdue window."));
                    continue;
                }

                if (!habitMap.TryGetValue(habitId, out var habit))
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: ErrorMessages.HabitNotFound));
                    continue;
                }

                // Validate schedule for recurring non-flexible habits
                if (habit.FrequencyUnit is not null && !habit.IsFlexible
                    && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Habit is not scheduled on this date."));
                    continue;
                }

                // Skip if already logged for target date (no toggle -- just skip)
                var alreadyLogged = habit.Logs.Any(l => l.Date == targetDate);
                if (alreadyLogged)
                {
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Success,
                        HabitId: habitId));
                    continue;
                }

                var shouldAdvanceDueDate = targetDate >= today;
                var logResult = habit.Log(targetDate, advanceDueDate: shouldAdvanceDueDate);
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

                results.Add(new BulkLogItemResult(
                    Index: i,
                    Status: BulkItemStatus.Success,
                    HabitId: habitId,
                    LogId: logResult.Value.Id));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing bulk item {HabitId}", habitId);
                results.Add(new BulkLogItemResult(
                    Index: i,
                    Status: BulkItemStatus.Failed,
                    HabitId: habitId,
                    Error: "An error occurred processing this item"));
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
            catch (Exception ex) { logger.LogWarning(ex, "Gamification processing failed for habit {HabitId}", item.HabitId); }
        }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(new BulkLogResult(results));
    }

}
