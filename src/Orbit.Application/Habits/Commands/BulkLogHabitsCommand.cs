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
    string? Error = null,
    string? ErrorCode = null);

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

        var habitIds = request.Items.Select(i => i.HabitId).ToHashSet();
        var loggableWindowStart = today.AddDays(-AppConstants.DefaultOverdueWindowDays);
        var habits = await habitRepository.FindTrackedAsync(
            h => habitIds.Contains(h.Id) && h.UserId == request.UserId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= loggableWindowStart)),
            cancellationToken);
        var habitMap = habits.ToDictionary(h => h.Id);

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                var targetDate = item.Date ?? today;

                try
                {
                    results.Add(await ProcessLogItem(i, item.HabitId, targetDate, today, habitMap, ct));
                }
                catch (Exception ex)
                {
                    LogBulkLogItemError(logger, ex, item.HabitId);
                    results.Add(new BulkLogItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: item.HabitId,
                        Error: ErrorMessages.BulkLogItemFailed.Message,
                        ErrorCode: ErrorMessages.BulkLogItemFailed.Code));
                }
            }

            await unitOfWork.SaveChangesAsync(ct);

            var loggedHabitIds = results
                .Where(r => r.Status == BulkItemStatus.Success && r.LogId is not null)
                .Select(r => r.HabitId)
                .ToList();
            if (loggedHabitIds.Count > 0)
            {
                try
                {
                    await services.GamificationService.ProcessHabitsLogged(request.UserId, loggedHabitIds, ct);
                }
                catch (Exception ex) { LogGamificationBulkLogFailed(logger, ex, request.UserId); }

                await ConcurrencyRetry.SaveWithRetryAsync(
                    unitOfWork,
                    c => services.UserStreakService.RecalculateAsync(request.UserId, c),
                    ct);
            }
        }, cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId);

        return Result.Success(new BulkLogResult(results));
    }

    private async Task<BulkLogItemResult> ProcessLogItem(
        int index, Guid habitId, DateOnly targetDate, DateOnly today,
        Dictionary<Guid, Habit> habitMap, CancellationToken cancellationToken)
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

        if (habit.FrequencyUnit is not null && !habit.IsFlexible
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
        {
            var isOverdue = targetDate == today && HabitScheduleService.HasMissedPastOccurrence(habit, today);
            if (!isOverdue)
                return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                    Error: ErrorMessages.NotScheduledOnDate.Message, ErrorCode: ErrorMessages.NotScheduledOnDate.Code);
        }

        if (habit.Logs.Any(l => l.Date == targetDate))
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);

        var shouldAdvanceDueDate = targetDate >= today;
        var logResult = habit.Log(targetDate, advanceDueDate: shouldAdvanceDueDate);
        if (logResult.IsFailure)
            return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: logResult.Error, ErrorCode: logResult.ErrorCode);

        await habitLogRepository.AddAsync(logResult.Value, cancellationToken);

        return new BulkLogItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId,
            LogId: logResult.Value.Id);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Error processing bulk item {HabitId}")]
    private static partial void LogBulkLogItemError(ILogger logger, Exception ex, Guid habitId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Gamification processing failed for bulk log by user {UserId}")]
    private static partial void LogGamificationBulkLogFailed(ILogger logger, Exception ex, Guid userId);
}
