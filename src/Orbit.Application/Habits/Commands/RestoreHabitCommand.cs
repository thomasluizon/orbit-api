using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record RestoreHabitCommand(
    Guid UserId,
    Guid HabitId) : IRequest<Result>, IConcurrencyRetryable;

public class RestoreHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IPayGateService payGate,
    IUserStreakService userStreakService,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<RestoreHabitCommand, Result>
{
    public Task<Result> Handle(RestoreHabitCommand request, CancellationToken cancellationToken) =>
        HabitCeilingLock.ExecuteEntryAsync(
            unitOfWork,
            request.UserId,
            payGate,
            ct => PrepareRestoreAsync(request, ct),
            state => HabitLiveRootEntry.FromRestore(state.Habit),
            (state, ct) => RestoreAsync(request.UserId, state, ct),
            cancellationToken);

    private async Task<Result<RestoreState>> PrepareRestoreAsync(
        RestoreHabitCommand request,
        CancellationToken cancellationToken)
    {
        var userHabits = await habitRepository.FindTrackedIgnoringFiltersAsync(
            h => h.UserId == request.UserId && h.IsDeleted,
            query => query.Include(h => h.Goals),
            cancellationToken);

        var habit = userHabits.FirstOrDefault(h => h.Id == request.HabitId);
        if (habit is null || !habit.IsDeleted)
            return Result.Failure<RestoreState>(ErrorMessages.HabitNotFound);

        var childrenByParentId = userHabits.ToLookup(h => h.ParentHabitId);
        var cascadeDeletedAtUtc = habit.DeletedAtUtc;
        var restoredHabits = HabitHierarchy.SelfAndDescendants(habit, childrenByParentId)
            .Where(inSubtree => inSubtree.IsDeleted && inSubtree.DeletedAtUtc == cascadeDeletedAtUtc)
            .ToList();

        return Result.Success(new RestoreState(habit, restoredHabits));
    }

    private async Task<Result> RestoreAsync(
        Guid userId,
        RestoreState state,
        CancellationToken cancellationToken)
    {
        var restoredHabits = state.RestoredHabits;
        var goalIds = restoredHabits
            .SelectMany(restoredHabit => restoredHabit.Goals)
            .Where(goal => !goal.IsDeleted && goal.UserId == userId && goal.IsProgressDerived)
            .Select(goal => goal.Id)
            .Distinct()
            .ToList();

        foreach (var inSubtree in restoredHabits)
            inSubtree.Restore();

        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        if (goalIds.Count > 0)
        {
            await goalCompletionService.SyncDerivedGoalsAsync(
                userId,
                goalIds,
                today,
                cancellationToken: cancellationToken);
        }
        else
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await ConcurrencyRetry.SaveWithRetryAsync(
            unitOfWork,
            ct => userStreakService.RecalculateAsync(userId, cancellationToken: ct),
            cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, userId, today);

        return Result.Success();
    }

    private sealed record RestoreState(Habit Habit, IReadOnlyList<Habit> RestoredHabits);
}
