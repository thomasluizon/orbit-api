using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
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
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<RestoreHabitCommand, Result>
{
    public async Task<Result> Handle(RestoreHabitCommand request, CancellationToken cancellationToken)
    {
        var userHabits = await habitRepository.FindTrackedIgnoringFiltersAsync(
            h => h.UserId == request.UserId && h.IsDeleted,
            cancellationToken);

        var habit = userHabits.FirstOrDefault(h => h.Id == request.HabitId);
        if (habit is null || !habit.IsDeleted)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var childrenByParentId = userHabits.ToLookup(h => h.ParentHabitId);
        var cascadeDeletedAtUtc = habit.DeletedAtUtc;

        foreach (var inSubtree in HabitHierarchy.SelfAndDescendants(habit, childrenByParentId))
            if (inSubtree.IsDeleted && inSubtree.DeletedAtUtc == cascadeDeletedAtUtc)
                inSubtree.Restore();

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await ConcurrencyRetry.SaveWithRetryAsync(
            unitOfWork,
            ct => userStreakService.RecalculateAsync(request.UserId, ct),
            cancellationToken);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }
}
