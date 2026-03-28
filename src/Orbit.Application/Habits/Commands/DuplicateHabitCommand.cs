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
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<DuplicateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(DuplicateHabitCommand request, CancellationToken cancellationToken)
    {
        // Load all habits for this user
        var allHabits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            cancellationToken);

        var original = allHabits.FirstOrDefault(h => h.Id == request.HabitId);
        if (original is null)
            return Result.Failure<Guid>(ErrorMessages.HabitNotFound);

        // Check plan limits
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is not null && !user.HasProAccess)
        {
            if (allHabits.Count >= 10)
                return Result.Failure<Guid>("You've reached the 10 habit limit on the free plan. Upgrade to Pro for unlimited habits.");

            // Duplicating a habit with children = creating sub-habits
            var childLookupCheck = allHabits.ToLookup(h => h.ParentHabitId);
            if (childLookupCheck[original.Id].Any())
                return Result.Failure<Guid>("Duplicating habits with sub-habits is a Pro feature. Upgrade to unlock!");
        }

        var childLookup = allHabits.ToLookup(h => h.ParentHabitId);

        // Duplicate the root habit
        var rootCopy = CloneHabit(original, original.ParentHabitId);
        if (rootCopy.IsFailure)
            return Result.Failure<Guid>(rootCopy.Error);

        await habitRepository.AddAsync(rootCopy.Value, cancellationToken);

        // Load original with tags (tracked so EF handles join table correctly)
        var originalTracked = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);

        if (originalTracked is not null)
        {
            foreach (var tag in originalTracked.Tags)
                rootCopy.Value.AddTag(tag);
        }

        // Recursively duplicate children (with tag copying)
        await DuplicateChildrenAsync(original.Id, rootCopy.Value.Id, childLookup, habitRepository, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(rootCopy.Value.Id);
    }

    private static async Task DuplicateChildrenAsync(
        Guid originalParentId,
        Guid newParentId,
        ILookup<Guid?, Habit> childLookup,
        IGenericRepository<Habit> repository,
        CancellationToken cancellationToken)
    {
        foreach (var child in childLookup[originalParentId])
        {
            var childCopy = CloneHabit(child, newParentId);
            if (childCopy.IsFailure) continue;

            await repository.AddAsync(childCopy.Value, cancellationToken);

            // Load child with tags (tracked) and copy them to the clone
            var childTracked = await repository.FindOneTrackedAsync(
                h => h.Id == child.Id,
                q => q.Include(h => h.Tags),
                cancellationToken);

            if (childTracked is not null)
            {
                foreach (var tag in childTracked.Tags)
                    childCopy.Value.AddTag(tag);
            }

            await DuplicateChildrenAsync(child.Id, childCopy.Value.Id, childLookup, repository, cancellationToken);
        }
    }

    private static Result<Habit> CloneHabit(Habit source, Guid? parentHabitId)
    {
        return Habit.Create(
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
            checklistItems: source.ChecklistItems,
            isGeneral: source.IsGeneral,
            isFlexible: source.IsFlexible,
            endDate: source.EndDate);
    }
}
