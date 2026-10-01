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
    Guid? SkipId = null) : IRequest<Result<SkipHabitResponse>>, IConcurrencyRetryable, IIdempotentCommand;

public sealed record SkipHabitResponse(Guid SkipId);

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
    IMemoryCache cache) : IRequestHandler<SkipHabitCommand, Result<SkipHabitResponse>>
{
    /**
     * A skip advances a due date or writes a skip log, both inputs a streak repair reads, so it
     * commits inside HabitCeilingLock like every other writer of that state.
     */
    public Task<Result<SkipHabitResponse>> Handle(SkipHabitCommand request, CancellationToken cancellationToken) =>
        HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            request.UserId,
            transactionToken => SkipAsync(request, transactionToken),
            cancellationToken);

    private async Task<Result<SkipHabitResponse>> SkipAsync(SkipHabitCommand request, CancellationToken cancellationToken)
    {
        if (request.SkipId is { } skipId)
        {
            var existing = await repos.SkipUndos.GetByIdAsync(skipId, cancellationToken);
            if (existing is not null)
                return existing.UserId == request.UserId && existing.HabitId == request.HabitId && !existing.IsUndone
                    ? Result.Success(new SkipHabitResponse(existing.Id))
                    : Result.Failure<SkipHabitResponse>(DomainErrors.SkipUndoConflict);
        }

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var loggableWindowStart = today.AddDays(-AppConstants.MaxRangeDays);

        var habit = await repos.Habits.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= loggableWindowStart))
                  .Include(h => h.Goals).Include(h => h.Tags)
                  .AsSplitQuery(),
            cancellationToken);

        if (habit is null)
            return Result.Failure<SkipHabitResponse>(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure<SkipHabitResponse>(ErrorMessages.HabitNotOwned);

        if (!await repos.Habits.TryRefreshAsync(habit, cancellationToken))
            return Result.Failure<SkipHabitResponse>(ErrorMessages.ConcurrentUpdateConflict);

        if (habit.IsCompleted)
            return Result.Failure<SkipHabitResponse>(ErrorMessages.CannotSkipCompletedHabit);

        var receipt = HabitSkipUndo.Create(request.SkipId ?? Guid.NewGuid(), habit);
        if (receipt.IsFailure)
            return receipt.PropagateError<SkipHabitResponse>();
        var previousLogIds = habit.Logs.Select(log => log.Id).ToHashSet();

        if (habit.FrequencyUnit is null)
        {
            habit.PostponeTo(today.AddDays(1));
            await SaveUndoAsync(receipt.Value, habit, previousLogIds, cancellationToken);
            CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId, today);
            return Result.Success(new SkipHabitResponse(receipt.Value.Id));
        }

        var targetDate = request.Date ?? today;

        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(request.UserId, cancellationToken);
        var dueDateResolution = await HabitDueDateResolutionLoader.LoadAsync(
            repos.HabitLogs,
            [habit],
            loggableWindowStart,
            cancellationToken);
        var validation = SkipHabitArgumentChecks.Check(habit, targetDate, today, weekStartDay,
            allowOverdue: true, dueDateResolved: dueDateResolution.Contains(habit.Id), enforceWindow: true);
        if (validation.IsFailure)
            return validation.PropagateError<SkipHabitResponse>();

        var skipError = await ApplySkip(habit, targetDate, weekStartDay, cancellationToken);
        if (skipError is not null)
            return skipError.PropagateError<SkipHabitResponse>();

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

        return Result.Success(new SkipHabitResponse(receipt.Value.Id));
    }

    private async Task SaveUndoAsync(
        HabitSkipUndo receipt, Habit habit, HashSet<Guid> previousLogIds, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var logState = await SkipUndoLogState.ReadAsync(repos.HabitLogs, habit, cancellationToken);
        var skipLogId = habit.Logs.Where(log => !previousLogIds.Contains(log.Id))
            .Select(log => (Guid?)log.Id).SingleOrDefault();
        receipt.Seal(habit, skipLogId, logState);
        await repos.SkipUndos.AddAsync(receipt, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public static async Task<Result> CheckArgumentsAsync(
        SkipHabitCommand request,
        IGenericRepository<Habit> habits,
        IGenericRepository<HabitLog> logs,
        IUserDateService dates,
        CancellationToken ct)
    {
        var matches = await habits.FindAsync(h => h.Id == request.HabitId && h.UserId == request.UserId,
            query => query.Include(h => h.Logs), ct);
        var habit = matches.FirstOrDefault();
        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);
        if (habit.IsCompleted)
            return Result.Failure(ErrorMessages.CannotSkipCompletedHabit);
        if (habit.FrequencyUnit is null)
            return Result.Success();

        var today = await dates.GetUserTodayAsync(request.UserId, ct);
        var weekStart = await dates.GetUserWeekStartDayAsync(request.UserId, ct);
        var resolution = await HabitDueDateResolutionLoader.LoadAsync(logs, [habit],
            today.AddDays(-AppConstants.MaxRangeDays), ct);
        return SkipHabitArgumentChecks.Check(habit, request.Date ?? today, today, weekStart,
            allowOverdue: true, dueDateResolved: resolution.Contains(habit.Id), enforceWindow: true);
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
