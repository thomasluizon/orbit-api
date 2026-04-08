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

/// <summary>
/// Groups supporting services for bulk habit logging to reduce constructor parameter count (S107).
/// </summary>
public record BulkLogServices(
    IUserDateService UserDateService,
    IUserStreakService UserStreakService,
    IGamificationService GamificationService);

public partial class BulkLogHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    BulkLogServices services,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkLogHabitsCommandHandler> logger) : IRequestHandler<BulkLogHabitsCommand, Result<BulkLogResult>>
{
    public async Task<Result<BulkLogResult>> Handle(BulkLogHabitsCommand request, CancellationToken cancellationToken)
    {
        var today = await services.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);
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
            var targetDate = item.Date ?? today;

            try
            {
                results.Add(await ProcessLogItem(i, item.HabitId, targetDate, today, habitMap, cancellationToken));
            }
            catch (Exception ex)
            {
                LogBulkLogItemError(logger, ex, item.HabitId);
                results.Add(new BulkLogItemResult(
                    Index: i,
                    Status: BulkItemStatus.Failed,
                    HabitId: item.HabitId,
                    Error: "An error occurred processing this item"));
            }
        }

        // Save all successful logs once
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (results.Any(r => r.Status == BulkItemStatus.Success && r.LogId is not null))
            await services.UserStreakService.RecalculateAsync(request.UserId, cancellationToken);

        // Gamification: process each successfully logged habit
        var loggedHabitIds = results
            .Where(r => r.Status == BulkItemStatus.Success && r.LogId is not null)
            .Select(r => r.HabitId);
        foreach (var habitId in loggedHabitIds)
        {
            try
            {
                await services.GamificationService.ProcessHabitLogged(request.UserId, habitId, cancellationToken);
            }
            catch (Exception ex) { LogGamificationBulkLogFailed(logger, ex, habitId); }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(new BulkLogResult(results));
    }

    private async Task<BulkLogItemResult> ProcessLogItem(
        int index, Guid habitId, DateOnly targetDate, DateOnly today,
        Dictionary<Guid, Habit> habitMap, CancellationToken cancellationToken)
    {
        if (targetDate > today)
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: "Cannot log a future date.");

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: "Cannot log a date beyond the overdue window.");

        if (!habitMap.TryGetValue(habitId, out var habit))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.HabitNotFound);

        // Validate schedule for recurring non-flexible habits
        if (habit.FrequencyUnit is not null && !habit.IsFlexible
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: "Habit is not scheduled on this date.");

        // Skip if already logged for target date (no toggle -- just skip)
        if (habit.Logs.Any(l => l.Date == targetDate))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);

        var shouldAdvanceDueDate = targetDate >= today;
        var logResult = habit.Log(targetDate, advanceDueDate: shouldAdvanceDueDate);
        if (logResult.IsFailure)
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, cancellationToken);

        return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId,
            LogId: logResult.Value.Id);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Error processing bulk item {HabitId}")]
    private static partial void LogBulkLogItemError(ILogger logger, Exception ex, Guid habitId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Gamification processing failed for habit {HabitId}")]
    private static partial void LogGamificationBulkLogFailed(ILogger logger, Exception ex, Guid habitId);
}
