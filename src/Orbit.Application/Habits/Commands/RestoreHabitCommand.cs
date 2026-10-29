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
        var habit = await habitRepository.FindOneTrackedIgnoringFiltersAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId && h.IsDeleted,
            cancellationToken);
        if (habit is null)
            return Result.Failure<RestoreState>(ErrorMessages.HabitNotFound);

        var cascadeDeletedAtUtc = habit.DeletedAtUtc;
        var restoredIds = new HashSet<Guid> { habit.Id };
        var visitedIds = new HashSet<Guid> { habit.Id };
        var parentIds = new List<Guid> { habit.Id };
        while (parentIds.Count > 0)
        {
            var children = await habitRepository.FindIgnoringFiltersAsync(
                h => h.UserId == request.UserId && h.IsDeleted
                    && h.ParentHabitId.HasValue && parentIds.Contains(h.ParentHabitId.Value),
                cancellationToken: cancellationToken);
            var nextParentIds = new List<Guid>();
            foreach (var child in children)
            {
                if (!visitedIds.Add(child.Id))
                    continue;

                nextParentIds.Add(child.Id);
                if (child.DeletedAtUtc == cascadeDeletedAtUtc)
                    restoredIds.Add(child.Id);
            }
            parentIds = nextParentIds;
        }

        var restoredHabits = await habitRepository.FindTrackedIgnoringFiltersAsync(
            h => h.UserId == request.UserId && h.IsDeleted && restoredIds.Contains(h.Id),
            query => query.Include(h => h.Goals),
            cancellationToken);

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
