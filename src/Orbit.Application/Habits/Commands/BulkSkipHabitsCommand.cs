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
            var habitId = item.HabitId;
            var targetDate = item.Date ?? today;

            try
            {
                if (targetDate > today)
                {
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Cannot skip a future date."));
                    continue;
                }

                if (!habitMap.TryGetValue(habitId, out var habit))
                {
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: ErrorMessages.HabitNotFound));
                    continue;
                }

                if (habit.IsCompleted)
                {
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Cannot skip a completed habit."));
                    continue;
                }

                if (habit.FrequencyUnit is null)
                {
                    // One-time task: postpone to tomorrow
                    habit.PostponeTo(today.AddDays(1));
                    results.Add(new BulkSkipItemResult(Index: i, Status: BulkItemStatus.Success, HabitId: habitId));
                    continue;
                }

                if (!habit.IsFlexible && habit.DueDate > targetDate)
                {
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Cannot skip a habit that is not yet due."));
                    continue;
                }

                if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
                {
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Habit is not scheduled on this date."));
                    continue;
                }

                if (habit.IsFlexible)
                {
                    var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs);
                    if (remaining <= 0)
                    {
                        results.Add(new BulkSkipItemResult(
                            Index: i,
                            Status: BulkItemStatus.Failed,
                            HabitId: habitId,
                            Error: "All instances for this period have already been completed or skipped."));
                        continue;
                    }

                    var skipResult = habit.SkipFlexible(targetDate);
                    if (skipResult.IsFailure)
                    {
                        results.Add(new BulkSkipItemResult(
                            Index: i,
                            Status: BulkItemStatus.Failed,
                            HabitId: habitId,
                            Error: skipResult.Error));
                        continue;
                    }

                    await habitLogRepository.AddAsync(skipResult.Value, cancellationToken);
                }
                else
                {
                    habit.AdvanceDueDate(targetDate);
                }

                results.Add(new BulkSkipItemResult(
                    Index: i,
                    Status: BulkItemStatus.Success,
                    HabitId: habitId));
            }
            catch (Exception ex)
            {
                results.Add(new BulkSkipItemResult(
                    Index: i,
                    Status: BulkItemStatus.Failed,
                    HabitId: habitId,
                    Error: ex.Message));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(new BulkSkipResult(results));
    }
}
