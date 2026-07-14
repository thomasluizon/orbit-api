using MediatR;
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
    IReadOnlyList<BulkSkipItem> Items) : IRequest<Result<BulkSkipResult>>, IBulkHabitCommand<BulkSkipItem>;

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

        var habitMap = await BulkHabitLoader.LoadHabitsWithRecentLogsAsync(
            habitRepository, request.Items.Select(i => i.HabitId), request.UserId, today, cancellationToken);

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                var targetDate = item.Date ?? today;

                try
                {
                    results.Add(await ProcessSkipItem(i, item.HabitId, targetDate, today, weekStartDay, habitMap, ct));
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

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success(new BulkSkipResult(results));
    }

    private async Task<BulkSkipItemResult> ProcessSkipItem(
        int index, Guid habitId, DateOnly targetDate, DateOnly today, int weekStartDay,
        Dictionary<Guid, Habit> habitMap, CancellationToken cancellationToken)
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

        if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
        {
            var isOverdue = targetDate == today && HabitScheduleService.HasMissedPastOccurrence(habit, today);
            if (!isOverdue)
                return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                    Error: ErrorMessages.NotScheduledOnDate.Message, ErrorCode: ErrorMessages.NotScheduledOnDate.Code);
        }

        if (habit.IsFlexible)
            return await SkipFlexibleAsync(index, habitId, habit, targetDate, weekStartDay, cancellationToken);

        habit.AdvanceDueDate(targetDate);
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
