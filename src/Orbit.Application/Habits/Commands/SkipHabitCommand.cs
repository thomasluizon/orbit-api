using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
    DateOnly? Date = null) : IRequest<Result>, IConcurrencyRetryable, IIdempotentCommand;

/// <summary>Groups the repositories a habit skip touches to keep the handler constructor small.</summary>
public record SkipHabitRepositories(
    IGenericRepository<Habit> Habits,
    IGenericRepository<HabitLog> HabitLogs);

public class SkipHabitCommandHandler(
    SkipHabitRepositories repos,
    IUserDateService userDateService,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<SkipHabitCommand, Result>
{
    public async Task<Result> Handle(SkipHabitCommand request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var loggableWindowStart = today.AddDays(-AppConstants.DefaultOverdueWindowDays);

        var habit = await repos.Habits.FindOneTrackedAsync(
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

        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var validationError = ValidateSkipTarget(habit, targetDate, today, weekStartDay);
        if (validationError is not null)
            return validationError;

        var skipError = await ApplySkip(habit, targetDate, weekStartDay, cancellationToken);
        if (skipError is not null)
            return skipError;

        var userId = habit.UserId;
        var streakGoalIds = habit.Goals
            .Where(g => g.Type == GoalType.Streak && g.Status == GoalStatus.Active)
            .Select(g => g.Id)
            .ToList();
        await goalCompletionService.SyncDerivedGoalsAsync(
            userId,
            streakGoalIds,
            today,
            cancellationToken: cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, userId, today);

        return Result.Success();
    }

    private async Task<Result> HandleOneTimeSkip(Habit habit, DateOnly today, CancellationToken cancellationToken)
    {
        habit.PostponeTo(today.AddDays(1));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId, today);
        return Result.Success();
    }

    private static Result? ValidateSkipTarget(
        Habit habit,
        DateOnly targetDate,
        DateOnly today,
        int weekStartDay)
    {
        if (targetDate > today)
            return Result.Failure(ErrorMessages.CannotSkipFutureDate);

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return Result.Failure(ErrorMessages.BeyondOverdueWindow);

        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return Result.Failure(ErrorMessages.HabitNotYetDue);

        if (!HabitScheduleService.IsHabitDueOnDate(habit, targetDate, weekStartDay))
        {
            var isOverdue = !habit.IsFlexible
                && targetDate == today
                && HabitScheduleService.HasMissedPastOccurrence(habit, today);
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

            await repos.HabitLogs.AddAsync(skipResult.Value, cancellationToken);
        }
        else
        {
            habit.AdvanceDueDate(targetDate, weekStartDay);
        }

        return null;
    }

}
