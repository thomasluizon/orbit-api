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
    IUserStreakService userStreakService,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<RestoreHabitCommand, Result>
{
    public async Task<Result> Handle(RestoreHabitCommand request, CancellationToken cancellationToken)
    {
        var userHabits = await habitRepository.FindTrackedIgnoringFiltersAsync(
            h => h.UserId == request.UserId && h.IsDeleted,
            query => query.Include(h => h.Goals),
            cancellationToken);

        var habit = userHabits.FirstOrDefault(h => h.Id == request.HabitId);
        if (habit is null || !habit.IsDeleted)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var childrenByParentId = userHabits.ToLookup(h => h.ParentHabitId);
        var cascadeDeletedAtUtc = habit.DeletedAtUtc;
        var restoredHabits = HabitHierarchy.SelfAndDescendants(habit, childrenByParentId)
            .Where(inSubtree => inSubtree.IsDeleted && inSubtree.DeletedAtUtc == cascadeDeletedAtUtc)
            .ToList();
        foreach (var inSubtree in restoredHabits)
            inSubtree.Restore();

        var goalIds = restoredHabits
            .SelectMany(restoredHabit => restoredHabit.Goals)
            .Where(goal => !goal.IsDeleted && goal.UserId == request.UserId && goal.IsProgressDerived)
            .Select(goal => goal.Id)
            .Distinct()
            .ToList();

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        if (goalIds.Count > 0)
        {
            await goalCompletionService.SyncDerivedGoalsAsync(
                request.UserId,
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
            ct => userStreakService.RecalculateAsync(request.UserId, cancellationToken: ct),
            cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }
}
