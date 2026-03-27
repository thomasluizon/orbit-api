using MediatR;
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
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<BulkSkipHabitsCommand, Result<BulkSkipResult>>
{
    public async Task<Result<BulkSkipResult>> Handle(BulkSkipHabitsCommand request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var results = new List<BulkSkipItemResult>();

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

                var habit = await habitRepository.FindOneTrackedAsync(
                    h => h.Id == habitId,
                    cancellationToken: cancellationToken);

                if (habit is null)
                {
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: ErrorMessages.HabitNotFound));
                    continue;
                }

                if (habit.UserId != request.UserId)
                {
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: ErrorMessages.HabitNotOwned));
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
                    results.Add(new BulkSkipItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        HabitId: habitId,
                        Error: "Cannot skip a one-time task."));
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
                    habit.AdvanceDueDatePastWindow(today);
                else
                    habit.AdvanceDueDate(targetDate);

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
