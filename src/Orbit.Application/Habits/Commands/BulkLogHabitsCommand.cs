using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Services;
using Orbit.Application.Notifications;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkLogItem(Guid HabitId, DateOnly? Date = null) : IBulkHabitItem;

public record BulkLogHabitsCommand(
    Guid UserId,
    IReadOnlyList<BulkLogItem> Items) : IRequest<Result<BulkLogResult>>, IBulkHabitCommand<BulkLogItem>, IIdempotentCommand, IIdempotencyFingerprint
{
    public string IdempotencyFingerprint => BulkHabitCommandFingerprint.Create(Items, item => item.Date);
}

public record BulkLogResult(
    IReadOnlyList<BulkLogItemResult> Results,
    int? PreviousStreak = null,
    int? CurrentStreak = null);

public record BulkLogItemResult(
    int Index,
    BulkItemStatus Status,
    Guid HabitId,
    Guid? LogId = null,
    string? Error = null,
    string? ErrorCode = null);

/// <summary>
/// Groups supporting services for bulk habit logging to reduce constructor parameter count (S107).
/// </summary>
public record BulkLogServices(
    IUserDateService UserDateService,
    IUserStreakService UserStreakService,
    IGamificationService GamificationService,
    IGenericRepository<Notification> Notifications);

public partial class BulkLogHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    BulkLogServices services,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkLogHabitsCommandHandler> logger) : IRequestHandler<BulkLogHabitsCommand, Result<BulkLogResult>>
{
    public async Task<Result<BulkLogResult>> Handle(BulkLogHabitsCommand request, CancellationToken cancellationToken)
    {
        var today = await services.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var weekStartDay = await services.UserDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        /** A completion log and its due-date advance are both inputs a streak repair reads. */
        var attempt = await HabitCeilingLock.ExecuteAsync(unitOfWork, request.UserId, async ct =>
        {
            var results = new List<BulkLogItemResult>();
            Exception? firstItemError = null;
            var itemErrorCount = 0;
            var habitMap = await BulkHabitLoader.LoadHabitsWithRecentLogsAsync(
                habitRepository, habitLogRepository, request.Items.Select(i => i.HabitId), request.UserId, today, weekStartDay, ct);
            using var batchScope = new HabitLogBatchReadScope(request.UserId, habitMap);
            var parentBatch = habitRepository is ITrackedEntityLookup<Habit>
                ? new BadHabitParentSlipBatch(habitMap, habitRepository,
                    request.Items.Select(item => item.Date ?? today).Where(date => date <= today
                        && date >= today.AddDays(-AppConstants.DefaultOverdueWindowDays)).ToArray())
                : null;
            var dueDateResolution = await HabitDueDateResolutionLoader.LoadAsync(request.UserId,
                habitLogRepository, habitMap.Values, today.AddDays(-AppConstants.DefaultOverdueWindowDays), ct);
            var previousStreak = (await services.UserStreakService.CalculateAsync(request.UserId, ct))?.CurrentStreak ?? 0;
            var currentStreak = previousStreak;
            var derivedParentIds = new HashSet<Guid>();

            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                var targetDate = item.Date ?? today;

                try
                {
                    var result = await ProcessLogItem(
                        i,
                        item.HabitId,
                        targetDate,
                        today,
                        weekStartDay,
                        habitMap,
                        dueDateResolution,
                        ct);
                    if (result.Status == BulkItemStatus.Success && result.LogId is not null)
                    {
                        var parentLogs = await BadHabitParentSlipService.LogAsync(
                            habitMap[item.HabitId], targetDate, today, weekStartDay, habitRepository, ct, parentBatch);
                        if (parentLogs.IsFailure)
                        {
                            results.Add(new BulkLogItemResult(i, BulkItemStatus.Failed, item.HabitId,
                                Error: parentLogs.Error, ErrorCode: parentLogs.ErrorCode));
                            continue;
                        }
                        foreach (var derivedLog in parentLogs.Value)
                        {
                            await habitLogRepository.AddAsync(derivedLog, ct);
                            derivedParentIds.Add(derivedLog.HabitId);
                        }
                    }
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    firstItemError ??= ex;
                    itemErrorCount++;
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: item.HabitId,
                        Error: ErrorMessages.BulkLogItemFailed.Message,
                        ErrorCode: ErrorMessages.BulkLogItemFailed.Code));
                }
            }

            var changedHabitIds = results
                .Where(r => r.Status == BulkItemStatus.Success && r.LogId is not null)
                .Select(r => r.HabitId)
                .ToHashSet();
            await HabitReminderInbox.MarkReadBatchAsync(services.Notifications, request.UserId,
                results.Where(r => r.Status == BulkItemStatus.Success && r.LogId is not null)
                    .Select(r => (r.HabitId, request.Items[r.Index].Date ?? today)), ct);
            var goalIds = habitMap.Values
                .Where(h => changedHabitIds.Contains(h.Id))
                .SelectMany(h => h.Goals)
                .Select(g => g.Id)
                .ToHashSet();
            await goalCompletionService.SyncDerivedGoalsAsync(request.UserId, goalIds, today, cancellationToken: ct);

            var loggedHabitIds = changedHabitIds.Union(derivedParentIds).ToList();
            if (loggedHabitIds.Count > 0)
            {
                try
                {
                    await services.GamificationService.ProcessHabitsLogged(request.UserId, loggedHabitIds, ct);
                }
                catch (Exception ex) { LogGamificationBulkLogFailed(logger, ex, request.UserId); }

                await ConcurrencyRetry.SaveWithRetryAsync(
                    unitOfWork,
                    async c => currentStreak = (await services.UserStreakService.RecalculateAsync(
                        request.UserId, cancellationToken: c))?.CurrentStreak ?? 0,
                    ct);
            }
            return (Result: new BulkLogResult(results, previousStreak, currentStreak), FirstItemError: firstItemError, ItemErrorCount: itemErrorCount);
        }, cancellationToken);

        if (attempt.FirstItemError is { } error) LogBulkLogItemError(logger, error, attempt.ItemErrorCount);
        await CacheInvalidationHelper.InvalidateUserAiCachesAsync(cache, request.UserId, today, preserveDailyNarrative: true);

        return Result.Success(attempt.Result);
    }

    private async Task<BulkLogItemResult> ProcessLogItem(
        int index, Guid habitId, DateOnly targetDate, DateOnly today, int weekStartDay,
        Dictionary<Guid, Habit> habitMap,
        IReadOnlySet<Guid> dueDateResolution,
        CancellationToken cancellationToken)
    {
        if (targetDate > today)
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.CannotLogFutureDate.Message, ErrorCode: ErrorMessages.CannotLogFutureDate.Code);

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.BeyondOverdueWindow.Message, ErrorCode: ErrorMessages.BeyondOverdueWindow.Code);

        if (!habitMap.TryGetValue(habitId, out var habit))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.HabitNotFound.Message, ErrorCode: ErrorMessages.HabitNotFound.Code);

        if (habit.Schedule != HabitSchedule.Flexible
            && (habit.Schedule != HabitSchedule.General || habit.IsCompleted)
            && habit.Logs.Any(l => l.Date == targetDate && !l.IsDeleted && (!habit.IsBadHabit || l.Value > 0)))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);

        if (habit.Schedule is HabitSchedule.Recurring or HabitSchedule.Flexible
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate, weekStartDay))
        {
            var isOverdue = habit.Schedule != HabitSchedule.Flexible
                && targetDate == today
                && HabitScheduleService.HasMissedPastOccurrence(
                    habit,
                    today,
                    weekStartDay,
                    dueDateResolution.Contains(habit.Id));
            if (!isOverdue)
                return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                    Error: ErrorMessages.NotScheduledOnDate.Message, ErrorCode: ErrorMessages.NotScheduledOnDate.Code);
        }

        var shouldAdvanceDueDate = targetDate >= today;
        var logResult = habit.Log(
            targetDate,
            advanceDueDate: shouldAdvanceDueDate,
            weekStartDay: weekStartDay, loggedOnLocalDate: today);
        if (logResult.IsFailure)
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: logResult.Error, ErrorCode: logResult.ErrorCode);

        await habitLogRepository.AddAsync(logResult.Value, cancellationToken);

        return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId,
            LogId: logResult.Value.Id);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Bulk logging failed for {FailureCount} items")]
    private static partial void LogBulkLogItemError(ILogger logger, Exception ex, int failureCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Gamification processing failed for bulk log by user {UserId}")]
    private static partial void LogGamificationBulkLogFailed(ILogger logger, Exception ex, Guid userId);

}
