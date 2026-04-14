using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record DuplicateHabitCommand(
    Guid UserId,
    Guid HabitId) : IRequest<Result<Guid>>;

public class DuplicateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IPayGateService payGateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<DuplicateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(DuplicateHabitCommand request, CancellationToken cancellationToken)
    {
        // Load all habits for this user with tags pre-loaded to avoid N+1 queries
        var allHabits = await habitRepository.FindTrackedAsync(
            h => h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);

        var original = allHabits.FirstOrDefault(h => h.Id == request.HabitId);
        if (original is null)
            return Result.Failure<Guid>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        // Check plan limits
        var canCreate = await payGateService.CanCreateHabits(request.UserId, 1, cancellationToken);
        if (!canCreate.IsSuccess) return canCreate.PropagateError<Guid>();

        var childLookup = allHabits.ToLookup(h => h.ParentHabitId);

        // If original has sub-habits, also check sub-habit gate
        if (childLookup[original.Id].Any())
        {
            var canCreateSub = await payGateService.CanCreateSubHabits(request.UserId, cancellationToken);
            if (!canCreateSub.IsSuccess) return canCreateSub.PropagateError<Guid>();
        }

        // Compute next position for root copy within its parent group.
        var rootSiblings = allHabits.Where(h => h.ParentHabitId == original.ParentHabitId && !h.IsDeleted).ToList();
        var nextRootPosition = rootSiblings.Count == 0
            ? 0
            : rootSiblings.Max(h => h.Position ?? -1) + 1;

        // Duplicate the root habit
        var rootCopy = CloneHabit(original, original.ParentHabitId, nextRootPosition);
        if (rootCopy.IsFailure)
            return Result.Failure<Guid>(rootCopy.Error);

        await habitRepository.AddAsync(rootCopy.Value, cancellationToken);

        // Tags are already loaded from the initial query
        foreach (var tag in original.Tags)
            rootCopy.Value.AddTag(tag);

        // Recursively duplicate children (tags already pre-loaded)
        await DuplicateChildren(original.Id, rootCopy.Value.Id, childLookup, habitRepository, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(rootCopy.Value.Id);
    }

    private static async Task DuplicateChildren(
        Guid originalParentId,
        Guid newParentId,
        ILookup<Guid?, Habit> childLookup,
        IGenericRepository<Habit> repository,
        CancellationToken cancellationToken)
    {
        var childPosition = 0;
        foreach (var child in childLookup[originalParentId])
        {
            var childCopy = CloneHabit(child, newParentId, childPosition++);
            if (childCopy.IsFailure) continue;

            await repository.AddAsync(childCopy.Value, cancellationToken);

            // Tags are already pre-loaded from the initial query
            foreach (var tag in child.Tags)
                childCopy.Value.AddTag(tag);

            await DuplicateChildren(child.Id, childCopy.Value.Id, childLookup, repository, cancellationToken);
        }
    }

    private static Result<Habit> CloneHabit(Habit source, Guid? parentHabitId, int position)
    {
        return Habit.Create(new HabitCreateParams(
            source.UserId,
            source.Title,
            source.FrequencyUnit,
            source.FrequencyQuantity,
            source.Description,
            source.Days.ToList(),
            source.IsBadHabit,
            source.DueDate,
            source.DueTime,
            source.DueEndTime,
            parentHabitId,
            ChecklistItems: source.ChecklistItems,
            IsGeneral: source.IsGeneral,
            IsFlexible: source.IsFlexible,
            EndDate: source.EndDate,
            ScheduledReminders: source.ScheduledReminders,
            Position: position,
            Icon: source.Icon));
    }
}
