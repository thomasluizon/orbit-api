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
    IGenericRepository<HabitLog> habitLogRepository,
    IPayGateService payGateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<DuplicateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(DuplicateHabitCommand request, CancellationToken cancellationToken)
    {
        var allHabits = await habitRepository.FindTrackedAsync(
            h => h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);

        var original = allHabits.FirstOrDefault(h => h.Id == request.HabitId);
        if (original is null)
            return Result.Failure<Guid>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        var canCreate = await payGateService.CanCreateHabits(request.UserId, 1, cancellationToken);
        if (!canCreate.IsSuccess) return canCreate.PropagateError<Guid>();

        var childLookup = allHabits.ToLookup(h => h.ParentHabitId);

        if (childLookup[original.Id].Any())
        {
            var canCreateSub = await payGateService.CanCreateSubHabits(request.UserId, cancellationToken);
            if (!canCreateSub.IsSuccess) return canCreateSub.PropagateError<Guid>();
        }

        var rootSiblings = allHabits.Where(h => h.ParentHabitId == original.ParentHabitId && !h.IsDeleted).ToList();
        var nextRootPosition = rootSiblings.Count == 0
            ? 0
            : rootSiblings.Max(h => h.Position ?? -1) + 1;

        var completionLogsBySourceId = await LoadCompletionLogs(original, childLookup, cancellationToken);

        var rootCopy = CloneHabit(original, original.ParentHabitId, nextRootPosition, completionLogsBySourceId);
        if (rootCopy.IsFailure)
            return Result.Failure<Guid>(rootCopy.Error);

        await habitRepository.AddAsync(rootCopy.Value, cancellationToken);

        foreach (var tag in original.Tags)
            rootCopy.Value.AddTag(tag);

        await DuplicateChildren(original.Id, rootCopy.Value.Id, childLookup, completionLogsBySourceId, habitRepository, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId);

        return Result.Success(rootCopy.Value.Id);
    }

    private async Task<Dictionary<Guid, HabitLog>> LoadCompletionLogs(
        Habit original,
        ILookup<Guid?, Habit> childLookup,
        CancellationToken cancellationToken)
    {
        var completedOneTimeIds = EnumerateSubtree(original, childLookup)
            .Where(h => h.FrequencyUnit is null && h.IsCompleted)
            .Select(h => h.Id)
            .ToHashSet();

        if (completedOneTimeIds.Count == 0)
            return new Dictionary<Guid, HabitLog>();

        var completionLogs = await habitLogRepository.FindAsync(
            l => completedOneTimeIds.Contains(l.HabitId) && l.Value > 0,
            cancellationToken);

        return completionLogs
            .GroupBy(l => l.HabitId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Date).First());
    }

    private static IEnumerable<Habit> EnumerateSubtree(Habit root, ILookup<Guid?, Habit> childLookup)
    {
        yield return root;
        foreach (var child in childLookup[root.Id])
            foreach (var descendant in EnumerateSubtree(child, childLookup))
                yield return descendant;
    }

    private static async Task DuplicateChildren(
        Guid originalParentId,
        Guid newParentId,
        ILookup<Guid?, Habit> childLookup,
        IReadOnlyDictionary<Guid, HabitLog> completionLogsBySourceId,
        IGenericRepository<Habit> repository,
        CancellationToken cancellationToken)
    {
        var childPosition = 0;
        foreach (var child in childLookup[originalParentId])
        {
            var childCopy = CloneHabit(child, newParentId, childPosition++, completionLogsBySourceId);
            if (childCopy.IsFailure) continue;

            await repository.AddAsync(childCopy.Value, cancellationToken);

            foreach (var tag in child.Tags)
                childCopy.Value.AddTag(tag);

            await DuplicateChildren(child.Id, childCopy.Value.Id, childLookup, completionLogsBySourceId, repository, cancellationToken);
        }
    }

    private static Result<Habit> CloneHabit(
        Habit source,
        Guid? parentHabitId,
        int position,
        IReadOnlyDictionary<Guid, HabitLog> completionLogsBySourceId)
    {
        var created = Habit.Create(new HabitCreateParams(
            source.UserId,
            source.Title,
            source.FrequencyUnit,
            source.FrequencyQuantity,
            source.Description,
            Emoji: source.Emoji,
            Days: source.Days.ToList(),
            IsBadHabit: source.IsBadHabit,
            DueDate: source.DueDate,
            DueTime: source.DueTime,
            DueEndTime: source.DueEndTime,
            ParentHabitId: parentHabitId,
            ChecklistItems: source.ChecklistItems,
            IsGeneral: source.IsGeneral,
            IsFlexible: source.IsFlexible,
            EndDate: source.EndDate,
            ScheduledReminders: source.ScheduledReminders,
            Position: position));

        if (created.IsFailure)
            return created;

        if (!completionLogsBySourceId.TryGetValue(source.Id, out var completionLog))
            return created;

        var replayed = created.Value.Log(completionLog.Date, completionLog.Note);
        return replayed.IsFailure ? Result.Failure<Habit>(replayed.Error) : created;
    }
}
