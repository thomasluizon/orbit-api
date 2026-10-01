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
    DateOnly? Date = null,
    Guid? SkipId = null) : IRequest<Result>, IConcurrencyRetryable, IIdempotentCommand;

/// <summary>Groups the repositories a habit skip touches to keep the handler constructor small.</summary>
public record SkipHabitRepositories(
    IGenericRepository<Habit> Habits,
    IGenericRepository<HabitLog> HabitLogs,
    IGenericRepository<HabitSkipUndo> SkipUndos);

public class SkipHabitCommandHandler(
    SkipHabitRepositories repos,
    IUserDateService userDateService,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<SkipHabitCommand, Result>
{
    /**
     * A skip advances a due date or writes a skip log, both inputs a streak repair reads, so it
     * commits inside HabitCeilingLock like every other writer of that state.
     */
    public Task<Result> Handle(SkipHabitCommand request, CancellationToken cancellationToken) =>
        HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            request.UserId,
            transactionToken => SkipAsync(request, transactionToken),
            cancellationToken);

    private async Task<Result> SkipAsync(SkipHabitCommand request, CancellationToken cancellationToken)
    {
        if (request.SkipId is { } skipId)
        {
            var existing = await repos.SkipUndos.GetByIdAsync(skipId, cancellationToken);
            if (existing is not null)
                return existing.UserId == request.UserId && existing.HabitId == request.HabitId && !existing.IsUndone
                    ? Result.Success()
                    : Result.Failure(DomainErrors.SkipUndoConflict);
        }

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var loggableWindowStart = today.AddDays(-AppConstants.MaxRangeDays);

        var habit = await repos.Habits.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= loggableWindowStart))
                  .Include(h => h.Goals)
                  .AsSplitQuery(),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.HabitNotOwned);

        if (habit.IsCompleted)
            return Result.Failure(ErrorMessages.CannotSkipCompletedHabit);

        var receipt = HabitSkipUndo.Create(request.SkipId ?? Guid.NewGuid(), habit);
        if (receipt.IsFailure)
            return receipt.PropagateError();
        var previousLogIds = habit.Logs.Select(log => log.Id).ToHashSet();

        if (habit.FrequencyUnit is null)
        {
            habit.PostponeTo(today.AddDays(1));
            await SaveUndoAsync(receipt.Value, habit, previousLogIds, cancellationToken);
            CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId, today);
            return Result.Success();
        }

        var targetDate = request.Date ?? today;

        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var dueDateResolution = await HabitDueDateResolutionLoader.LoadAsync(
            repos.HabitLogs,
            [habit],
            loggableWindowStart,
            cancellationToken);
        var validationError = ValidateSkipTarget(
            habit,
            targetDate,
            today,
            weekStartDay,
            dueDateResolution.Contains(habit.Id));
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

        await SaveUndoAsync(receipt.Value, habit, previousLogIds, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, userId, today);

        return Result.Success();
    }

    private async Task SaveUndoAsync(
        HabitSkipUndo receipt, Habit habit, HashSet<Guid> previousLogIds, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var logState = await SkipUndoLogState.ReadAsync(repos.HabitLogs, habit.Id, cancellationToken);
        var skipLogId = habit.Logs.Where(log => !previousLogIds.Contains(log.Id))
            .Select(log => (Guid?)log.Id).SingleOrDefault();
        receipt.Seal(habit, skipLogId, logState);
        await repos.SkipUndos.AddAsync(receipt, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static Result? ValidateSkipTarget(
        Habit habit,
        DateOnly targetDate,
        DateOnly today,
        int weekStartDay,
        bool dueDateResolved)
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
                && HabitScheduleService.HasMissedPastOccurrence(
                    habit,
                    today,
                    weekStartDay,
                    dueDateResolved);
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
            var advancement = habit.AdvanceDueDate(targetDate, weekStartDay);
            if (advancement.IsFailure)
                return advancement;
        }

        return null;
    }

}
