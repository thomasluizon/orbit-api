using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkSkipItem(Guid HabitId, DateOnly? Date = null) : IBulkHabitItem;

public record BulkSkipHabitsCommand(
    Guid UserId,
    IReadOnlyList<BulkSkipItem> Items) : IRequest<Result<BulkSkipResult>>, IBulkHabitCommand<BulkSkipItem>, IIdempotentCommand, IIdempotencyFingerprint
{
    public string IdempotencyFingerprint => BulkHabitCommandFingerprint.Create(Items, item => item.Date);
}

public record BulkSkipResult(IReadOnlyList<BulkSkipItemResult> Results);

public record BulkSkipItemResult(
    int Index,
    BulkItemStatus Status,
    Guid HabitId,
    string? Error = null,
    string? ErrorCode = null);

public class BulkSkipHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<BulkSkipHabitsCommand, Result<BulkSkipResult>>
{
    public async Task<Result<BulkSkipResult>> Handle(BulkSkipHabitsCommand request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var results = new List<BulkSkipItemResult>();

        var refreshFailed = false;
        /** A skip advances a due date or writes a skip log, both inputs a streak repair reads. */
        await HabitCeilingLock.ExecuteAsync(unitOfWork, request.UserId, async ct =>
        {
            var habitMap = await BulkHabitLoader.LoadHabitsWithRecentLogsAsync(
                habitRepository, request.Items.Select(i => i.HabitId), request.UserId, today, ct);
            foreach (var habit in habitMap.Values)
            {
                if (!await habitRepository.TryRefreshAsync(habit, ct))
                {
                    refreshFailed = true;
                    return;
                }
            }
            var dueDateResolution = await HabitDueDateResolutionLoader.LoadAsync(
                habitLogRepository, habitMap.Values, today.AddDays(-AppConstants.MaxRangeDays), ct);

            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                var targetDate = item.Date ?? today;

                try
                {
                    results.Add(await ProcessSkipItem(
                        i,
                        item.HabitId,
                        targetDate,
                        today,
                        weekStartDay,
                        habitMap,
                        dueDateResolution,
                        ct));
                }
                catch (Exception)
                {
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: item.HabitId,
                        Error: ErrorMessages.MutationFailed.Message,
                        ErrorCode: ErrorMessages.MutationFailed.Code));
                }
            }

            await unitOfWork.SaveChangesAsync(ct);
        }, cancellationToken);

        if (refreshFailed)
            return Result.Failure<BulkSkipResult>(ErrorMessages.ConcurrentUpdateConflict);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success(new BulkSkipResult(results));
    }

    internal static async Task<Result> CheckArgumentsAsync(BulkSkipHabitsCommand request,
        IGenericRepository<Habit> habits, IGenericRepository<HabitLog> logs, IUserDateService dates, CancellationToken ct)
    {
        var today = await dates.GetUserTodayAsync(request.UserId, ct);
        var weekStart = await dates.GetUserWeekStartDayAsync(request.UserId, ct);
        var ids = request.Items.Select(item => item.HabitId).ToList();
        var selected = await habits.FindAsync(h => h.UserId == request.UserId && ids.Contains(h.Id), q => q.Include(h => h.Logs), ct);
        var ownership = OwnershipValidation.AllResolved(ids, selected, h => h.Id, ErrorMessages.HabitNotFound);
        if (ownership.IsFailure)
            return ownership;
        var resolution = await HabitDueDateResolutionLoader.LoadAsync(logs, selected, today.AddDays(-AppConstants.MaxRangeDays), ct);
        var byId = selected.ToDictionary(h => h.Id);
        foreach (var item in request.Items)
        {
            var result = SkipHabitArgumentChecks.Check(byId[item.HabitId], item.Date ?? today, today, weekStart,
                allowOverdue: true, resolution.Contains(item.HabitId), enforceWindow: true);
            if (result.IsFailure)
                return result;
        }
        return Result.Success();
    }

    private async Task<BulkSkipItemResult> ProcessSkipItem(
        int index, Guid habitId, DateOnly targetDate, DateOnly today, int weekStartDay,
        Dictionary<Guid, Habit> habitMap,
        IReadOnlySet<Guid> dueDateResolution,
        CancellationToken cancellationToken)
    {
        if (targetDate > today)
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.CannotSkipFutureDate.Message, ErrorCode: ErrorMessages.CannotSkipFutureDate.Code);

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.BeyondOverdueWindow.Message, ErrorCode: ErrorMessages.BeyondOverdueWindow.Code);

        if (!habitMap.TryGetValue(habitId, out var habit))
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.HabitNotFound.Message, ErrorCode: ErrorMessages.HabitNotFound.Code);

        if (habit.IsCompleted)
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.CannotSkipCompletedHabit.Message, ErrorCode: ErrorMessages.CannotSkipCompletedHabit.Code);

        if (habit.FrequencyUnit is null)
        {
            habit.PostponeTo(today.AddDays(1));
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);
        }

        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.HabitNotYetDue.Message, ErrorCode: ErrorMessages.HabitNotYetDue.Code);

        if (!HabitScheduleService.IsHabitDueOnDate(habit, targetDate, weekStartDay))
        {
            var isOverdue = !habit.IsFlexible
                && targetDate == today
                && HabitScheduleService.HasMissedPastOccurrence(
                    habit,
                    today,
                    weekStartDay,
                    dueDateResolution.Contains(habit.Id));
            if (!isOverdue)
                return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                    Error: ErrorMessages.NotScheduledOnDate.Message, ErrorCode: ErrorMessages.NotScheduledOnDate.Code);
        }

        if (habit.IsFlexible)
            return await SkipFlexibleAsync(index, habitId, habit, targetDate, weekStartDay, cancellationToken);

        habit.AdvanceDueDate(targetDate, weekStartDay);
        return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);
    }

    private async Task<BulkSkipItemResult> SkipFlexibleAsync(
        int index, Guid habitId, Habit habit, DateOnly targetDate, int weekStartDay, CancellationToken cancellationToken)
    {
        var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs, weekStartDay);
        if (remaining <= 0)
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.AllInstancesDone.Message, ErrorCode: ErrorMessages.AllInstancesDone.Code);

        var skipResult = habit.SkipFlexible(targetDate);
        if (skipResult.IsFailure)
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: skipResult.Error, ErrorCode: skipResult.ErrorCode);

        await habitLogRepository.AddAsync(skipResult.Value, cancellationToken);
        return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);
    }
}
