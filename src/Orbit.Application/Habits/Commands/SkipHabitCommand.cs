using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record SkipHabitCommand(
    Guid UserId,
    Guid HabitId,
    DateOnly? Date = null) : IRequest<Result>;

public class SkipHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<SkipHabitCommand, Result>
{
    public async Task<Result> Handle(SkipHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs).Include(h => h.Goals),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.HabitNotOwned, ErrorCodes.HabitNotOwned);

        if (habit.IsCompleted)
            return Result.Failure("Cannot skip a completed habit.");

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        if (habit.FrequencyUnit is null)
        {
            // One-time task: postpone to tomorrow
            habit.PostponeTo(today.AddDays(1));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);
            return Result.Success();
        }
        var targetDate = request.Date ?? today;

        // Validate target date
        if (targetDate > today)
            return Result.Failure("Cannot skip a future date.");

        // For flexible habits, skip means record a skip log (Value=0) to reduce the period target
        // For regular habits, they must be due on or before the target date
        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return Result.Failure("Cannot skip a habit that is not yet due.");

        // Validate the habit is actually scheduled on the target date (for non-flexible)
        if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
            return Result.Failure("Habit is not scheduled on this date.");

        if (habit.IsFlexible)
        {
            var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs);
            if (remaining <= 0)
                return Result.Failure("All instances for this period have already been completed or skipped.");

            var skipResult = habit.SkipFlexible(targetDate);
            if (skipResult.IsFailure)
                return Result.Failure(skipResult.Error);

            await habitLogRepository.AddAsync(skipResult.Value, cancellationToken);
        }
        else
        {
            habit.AdvanceDueDate(targetDate);
        }

        // Sync streak goals linked to this habit
        if (habit.Goals.Count > 0)
        {
            var goalIds = habit.Goals.Select(g => g.Id).ToHashSet();
            var trackedGoals = await goalRepository.FindTrackedAsync(
                g => goalIds.Contains(g.Id), cancellationToken);

            var streakGoals = trackedGoals
                .Where(g => g.Type == GoalType.Streak && g.Status == GoalStatus.Active)
                .ToList();

            if (streakGoals.Count > 0)
            {
                var metrics = HabitMetricsCalculator.Calculate(habit, today);
                foreach (var streakGoal in streakGoals)
                    streakGoal.SyncStreakProgress(metrics.CurrentStreak);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

        return Result.Success();
    }
}
