using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

/// <param name="InheritParentFrequency">
/// When true, unset cadence fields inherit from the parent. The parent's week interval is inherited
/// only when the child supplies no cadence field of its own.
/// Only the AI chat tool (which treats an unspecified frequency as "match the parent") opts into
/// this; the REST API always sends the user's explicit choice, where an unset frequency means the
/// user picked "one-time".
/// </param>
public record CreateSubHabitCommand(
    Guid UserId,
    Guid ParentHabitId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit = null,
    int? FrequencyQuantity = null,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    HabitCommandOptions? Options = null,
    IReadOnlyList<Guid>? TagIds = null,
    string? Emoji = null,
    bool InheritParentFrequency = false,
    int? IntervalWeeks = null) : IRequest<Result<Guid>>;

public class CreateSubHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IAppConfigService appConfigService,
    IMemoryCache cache) : IRequestHandler<CreateSubHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateSubHabitCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.ParentHabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (parent is null)
            return Result.Failure<Guid>(ErrorMessages.ParentHabitNotFound);

        var maxDepth = await appConfigService.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, cancellationToken);
        var depth = await GetDepthAsync(parent, habitRepository, cancellationToken);
        if (depth >= maxDepth - 1)
            return Result.Failure<Guid>(ErrorMessages.MaxDepthReached.Format(maxDepth));

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var childDueDate = request.DueDate
            ?? (parent.DueDate > userToday ? parent.DueDate : userToday);

        var opts = request.Options ?? new HabitCommandOptions();

        var siblings = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.ParentHabitId == request.ParentHabitId && !h.IsDeleted,
            cancellationToken);
        var nextPosition = siblings.Count == 0
            ? 0
            : siblings.Max(h => h.Position ?? -1) + 1;

        var frequencyUnit = request.FrequencyUnit;
        var frequencyQuantity = request.FrequencyQuantity;
        var intervalWeeks = request.IntervalWeeks;
        var days = opts.Days;
        var hasExplicitCadence = frequencyUnit is not null
            || frequencyQuantity is not null
            || intervalWeeks is not null
            || days is not null;
        if (request.InheritParentFrequency)
        {
            frequencyUnit ??= parent.FrequencyUnit;
            frequencyQuantity ??= parent.FrequencyQuantity;
            days ??= parent.Days.ToList();
            if (!hasExplicitCadence)
                intervalWeeks = parent.IntervalWeeks;
        }

        var childResult = Habit.Create(new HabitCreateParams(
            request.UserId,
            request.Title,
            frequencyUnit,
            frequencyQuantity,
            childDueDate,
            request.Description,
            Emoji: request.Emoji,
            Days: days,
            IsBadHabit: request.IsBadHabit,
            DueTime: opts.DueTime,
            DueEndTime: opts.DueEndTime,
            ParentHabitId: parent.Id,
            ReminderEnabled: opts.ReminderEnabled,
            ReminderTimes: opts.ReminderTimes,
            SlipAlertEnabled: opts.SlipAlertEnabled,
            ChecklistItems: opts.ChecklistItems,
            IsGeneral: parent.IsGeneral,
            IsFlexible: opts.IsFlexible,
            EndDate: opts.EndDate,
            ScheduledReminders: opts.ScheduledReminders,
            Position: nextPosition,
            IntervalWeeks: intervalWeeks));

        if (childResult.IsFailure)
            return childResult.PropagateError<Guid>();

        var child = childResult.Value;

        if (request.TagIds is { Count: > 0 })
        {
            var tags = await tagRepository.FindTrackedAsync(
                t => request.TagIds.Contains(t.Id) && t.UserId == request.UserId,
                cancellationToken);

            var tagsResolved = OwnershipValidation.AllResolved(request.TagIds, tags, t => t.Id, ErrorMessages.TagNotFound);
            if (tagsResolved.IsFailure)
                return tagsResolved.PropagateError<Guid>();

            foreach (var tag in tags)
                child.AddTag(tag);
        }

        await habitRepository.AddAsync(child, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, userToday);

        return Result.Success(childResult.Value.Id);
    }

    private static async Task<int> GetDepthAsync(Habit habit, IGenericRepository<Habit> repo, CancellationToken ct)
    {
        if (habit.ParentHabitId is null) return 0;

        var allHabits = await repo.FindAsync(h => h.UserId == habit.UserId, ct);
        var habitDict = allHabits.ToDictionary(h => h.Id);

        var depth = 0;
        var currentId = habit.ParentHabitId;
        while (currentId is not null && habitDict.TryGetValue(currentId.Value, out var parent))
        {
            depth++;
            currentId = parent.ParentHabitId;
        }
        return depth;
    }

}
