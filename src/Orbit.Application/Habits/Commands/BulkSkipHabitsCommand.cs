using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkSkipItem(Guid HabitId, DateOnly? Date = null);

public record BulkSkipHabitsCommand(
    Guid UserId,
    IReadOnlyList<BulkSkipItem> Items) : IRequest<Result<BulkSkipResult>>;

public record BulkSkipResult(IReadOnlyList<BulkSkipItemResult> Results);

public record BulkSkipItemResult(
    int Index,
    BulkItemStatus Status,
    Guid HabitId,
    string? Error = null);

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
        var results = new List<BulkSkipItemResult>();

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
                results.Add(await ProcessSkipItem(i, item.HabitId, targetDate, today, habitMap, cancellationToken));
            }
            catch (Exception)
            {
                results.Add(new BulkSkipItemResult(
                    Index: i,
                    Status: BulkItemStatus.Failed,
                    HabitId: item.HabitId,
                    Error: "Mutation failed"));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(new BulkSkipResult(results));
    }

    private async Task<BulkSkipItemResult> ProcessSkipItem(
        int index, Guid habitId, DateOnly targetDate, DateOnly today,
        Dictionary<Guid, Habit> habitMap, CancellationToken cancellationToken)
    {
        if (targetDate > today)
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: "Cannot skip a future date.");

        if (!habitMap.TryGetValue(habitId, out var habit))
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: ErrorMessages.HabitNotFound);

        if (habit.IsCompleted)
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: "Cannot skip a completed habit.");

        if (habit.FrequencyUnit is null)
        {
            habit.PostponeTo(today.AddDays(1));
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);
        }

        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: "Cannot skip a habit that is not yet due.");

        if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
            return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                Error: "Habit is not scheduled on this date.");

        if (habit.IsFlexible)
        {
            var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs);
            if (remaining <= 0)
                return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                    Error: "All instances for this period have already been completed or skipped.");

            var skipResult = habit.SkipFlexible(targetDate);
            if (skipResult.IsFailure)
                return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Failed, HabitId: habitId,
                    Error: skipResult.Error);

            await habitLogRepository.AddAsync(skipResult.Value, cancellationToken);
        }
        else
        {
            habit.AdvanceDueDate(targetDate);
        }

        return new BulkSkipItemResult(Index: index, Status: BulkItemStatus.Success, HabitId: habitId);
    }
}
