using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
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
            return await HandleOneTimeSkip(habit, today, cancellationToken);

        var targetDate = request.Date ?? today;

        var validationError = ValidateSkipTarget(habit, targetDate, today);
        if (validationError is not null)
            return validationError;

        var skipError = await ApplySkip(habit, targetDate, cancellationToken);
        if (skipError is not null)
            return skipError;

        await SyncStreakGoals(habit, today, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId);

        return Result.Success();
    }

    private async Task<Result> HandleOneTimeSkip(Habit habit, DateOnly today, CancellationToken cancellationToken)
    {
        habit.PostponeTo(today.AddDays(1));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId);
        return Result.Success();
    }

    private static Result? ValidateSkipTarget(Habit habit, DateOnly targetDate, DateOnly today)
    {
        if (targetDate > today)
            return Result.Failure("Cannot skip a future date.");

        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return Result.Failure("Cannot skip a habit that is not yet due.");

        if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
            return Result.Failure("Habit is not scheduled on this date.");

        return null;
    }

    private async Task<Result?> ApplySkip(Habit habit, DateOnly targetDate, CancellationToken cancellationToken)
    {
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

        return null;
    }

    private async Task SyncStreakGoals(Habit habit, DateOnly today, CancellationToken cancellationToken)
    {
        if (habit.Goals.Count == 0) return;

        var goalIds = habit.Goals.Select(g => g.Id).ToHashSet();
        var trackedGoals = await goalRepository.FindTrackedAsync(
            g => goalIds.Contains(g.Id),
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken);

        var streakGoals = trackedGoals
            .Where(g => g.Type == GoalType.Streak && g.Status == GoalStatus.Active)
            .ToList();

        if (streakGoals.Count == 0) return;

        foreach (var streakGoal in streakGoals)
            GoalStreakSyncService.SyncCurrentStreak(streakGoal, today);
    }
}
