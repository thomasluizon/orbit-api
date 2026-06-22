using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Behaviors;
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
    DateOnly? Date = null) : IRequest<Result>, IConcurrencyRetryable;

public partial class SkipHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<SkipHabitCommandHandler> logger) : IRequestHandler<SkipHabitCommand, Result>
{
    public async Task<Result> Handle(SkipHabitCommand request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var loggableWindowStart = today.AddDays(-AppConstants.DefaultOverdueWindowDays);

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= loggableWindowStart)).Include(h => h.Goals),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.HabitNotOwned);

        if (habit.IsCompleted)
            return Result.Failure(ErrorMessages.CannotSkipCompletedHabit);

        if (habit.FrequencyUnit is null)
            return await HandleOneTimeSkip(habit, today, cancellationToken);

        var targetDate = request.Date ?? today;

        var validationError = ValidateSkipTarget(habit, targetDate, today);
        if (validationError is not null)
            return validationError;

        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var skipError = await ApplySkip(habit, targetDate, weekStartDay, cancellationToken);
        if (skipError is not null)
            return skipError;

        var anyGoalJustCompleted = await SyncStreakGoals(habit, today, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (anyGoalJustCompleted)
            await ProcessGoalCompletionSafeAsync(habit.UserId, cancellationToken);

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
            return Result.Failure(ErrorMessages.CannotSkipFutureDate);

        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return Result.Failure(ErrorMessages.HabitNotYetDue);

        if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
        {
            var isOverdue = targetDate == today && HabitScheduleService.HasMissedPastOccurrence(habit, today);
            if (!isOverdue)
                return Result.Failure(ErrorMessages.NotScheduledOnDate);
        }

        return null;
    }

    private async Task<Result?> ApplySkip(Habit habit, DateOnly targetDate, int weekStartDay, CancellationToken cancellationToken)
    {
        if (habit.IsFlexible)
        {
            var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs, weekStartDay);
            if (remaining <= 0)
                return Result.Failure(ErrorMessages.AllInstancesDone);

            var skipResult = habit.SkipFlexible(targetDate);
            if (skipResult.IsFailure)
                return skipResult.PropagateError();

            await habitLogRepository.AddAsync(skipResult.Value, cancellationToken);
        }
        else
        {
            habit.AdvanceDueDate(targetDate);
        }

        return null;
    }

    private async Task<bool> SyncStreakGoals(Habit habit, DateOnly today, CancellationToken cancellationToken)
    {
        if (habit.Goals.Count == 0) return false;

        var goalIds = habit.Goals.Select(g => g.Id).ToHashSet();
        var streakWindowStart = today.AddDays(-AppConstants.MaxStreakLookbackDays);
        var trackedGoals = await goalRepository.FindTrackedAsync(
            g => goalIds.Contains(g.Id),
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs.Where(l => l.Date >= streakWindowStart)),
            cancellationToken);

        var streakGoals = trackedGoals
            .Where(g => g.Type == GoalType.Streak && g.Status == GoalStatus.Active)
            .ToList();

        if (streakGoals.Count == 0) return false;

        var anyJustCompleted = false;
        foreach (var streakGoal in streakGoals)
            anyJustCompleted |= GoalStreakSyncService.SyncCurrentStreak(streakGoal, today).JustCompleted;

        return anyJustCompleted;
    }

    private async Task ProcessGoalCompletionSafeAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await gamificationService.ProcessGoalCompleted(userId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGamificationGoalCompletionFailed(logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for linked goal completion by user {UserId}")]
    private static partial void LogGamificationGoalCompletionFailed(ILogger logger, Exception ex, Guid userId);
}
