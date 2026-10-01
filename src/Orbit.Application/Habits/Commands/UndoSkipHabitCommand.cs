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

public sealed record UndoSkipHabitCommand(Guid UserId, Guid HabitId, Guid SkipId)
    : IRequest<Result>, IConcurrencyRetryable, IIdempotentCommand;

public sealed class UndoSkipHabitCommandHandler(
    SkipHabitRepositories repos,
    IUserDateService userDateService,
    IGoalCompletionService goalCompletionService,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    IHabitSkipUndoWriter undoWriter,
    IMemoryCache cache) : IRequestHandler<UndoSkipHabitCommand, Result>
{
    public Task<Result> Handle(UndoSkipHabitCommand request, CancellationToken cancellationToken) =>
        HabitCeilingLock.ExecuteAsync(unitOfWork, request.UserId,
            ct => UndoAsync(request, ct), cancellationToken);

    public static async Task<Result> CheckArgumentsAsync(
        UndoSkipHabitCommand request, SkipHabitRepositories repos, CancellationToken ct)
    {
        var receipts = await repos.SkipUndos.FindAsync(
            skip => skip.Id == request.SkipId && skip.HabitId == request.HabitId && skip.UserId == request.UserId, ct);
        var receipt = receipts.FirstOrDefault();
        if (receipt is null)
            return Result.Failure(DomainErrors.SkipNotFound);
        if (receipt.IsUndone)
            return Result.Success();
        var habits = await repos.Habits.FindAsync(h => h.Id == request.HabitId && h.UserId == request.UserId,
            query => query.Include(h => h.Tags).Include(h => h.Goals), ct);
        var habit = habits.FirstOrDefault();
        return habit is null ? Result.Failure(ErrorMessages.HabitNotFound)
            : receipt.CheckUndo(habit, await SkipUndoLogState.ReadAsync(repos.HabitLogs, habit, ct));
    }

    private async Task<Result> UndoAsync(UndoSkipHabitCommand request, CancellationToken ct)
    {
        var receipt = await repos.SkipUndos.FindOneTrackedAsync(
            skip => skip.Id == request.SkipId && skip.HabitId == request.HabitId && skip.UserId == request.UserId,
            cancellationToken: ct);
        if (receipt is null)
            return Result.Failure(DomainErrors.SkipNotFound);
        if (receipt.IsUndone)
            return Result.Success();

        var habit = await repos.Habits.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            query => query.Include(h => h.Logs).Include(h => h.Goals).Include(h => h.Tags).AsSplitQuery(), ct);
        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var logState = await SkipUndoLogState.ReadAsync(repos.HabitLogs, habit, ct);
        var guard = receipt.CheckUndo(habit, logState);
        if (guard.IsFailure)
            return guard;

        if (habit.IsCompleted && !receipt.PreviousIsCompleted && habit.ParentHabitId is null)
        {
            var allowance = await payGate.CanCreateHabits(request.UserId, 1, ct);
            if (allowance.IsFailure)
                return allowance;
        }

        var restored = receipt.Undo(habit, logState);
        if (restored.IsFailure)
            return restored;

        await undoWriter.SaveAsync(habit, receipt.ExpectedUpdatedAtUtc, ct);
        await unitOfWork.SaveChangesAsync(ct);
        var today = await userDateService.GetUserTodayAsync(request.UserId, ct);
        var streakGoalIds = habit.Goals.Where(goal => goal.Type == GoalType.Streak && goal.Status == GoalStatus.Active)
            .Select(goal => goal.Id).ToList();
        await goalCompletionService.SyncDerivedGoalsAsync(request.UserId, streakGoalIds, today,
            cancellationToken: ct);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);
        return Result.Success();
    }
}
