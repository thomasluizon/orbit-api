using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Commands;

public record CreateSubHabitCommand(
    Guid UserId,
    Guid ParentHabitId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit = null,
    int? FrequencyQuantity = null,
    IReadOnlyList<System.DayOfWeek>? Days = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool IsBadHabit = false,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    bool SlipAlertEnabled = false,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    IReadOnlyList<Guid>? TagIds = null,
    DateOnly? EndDate = null,
    bool IsFlexible = false,
    DateOnly? DueDate = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null) : IRequest<Result<Guid>>;

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
        // Sub-habits are a Pro feature
        var gateCheck = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.ParentHabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (parent is null)
            return Result.Failure<Guid>(ErrorMessages.ParentHabitNotFound);

        // Enforce max nesting depth from config
        var maxDepth = await appConfigService.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, cancellationToken);
        var depth = await GetDepthAsync(parent, habitRepository, cancellationToken);
        if (depth >= maxDepth - 1)
            return Result.Failure<Guid>($"Maximum nesting depth reached ({maxDepth} levels).");

        // Use explicit DueDate if provided, otherwise derive from parent
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var childDueDate = request.DueDate
            ?? (parent.DueDate > userToday ? parent.DueDate : userToday);

        var childResult = Habit.Create(
            request.UserId,
            request.Title,
            request.FrequencyUnit ?? parent.FrequencyUnit,
            request.FrequencyQuantity ?? parent.FrequencyQuantity,
            request.Description,
            days: request.Days,
            isBadHabit: request.IsBadHabit,
            dueDate: childDueDate,
            dueTime: request.DueTime,
            dueEndTime: request.DueEndTime,
            parentHabitId: parent.Id,
            reminderEnabled: request.ReminderEnabled,
            reminderTimes: request.ReminderTimes,
            slipAlertEnabled: request.SlipAlertEnabled,
            checklistItems: request.ChecklistItems,
            isGeneral: parent.IsGeneral,
            isFlexible: request.IsFlexible,
            endDate: request.EndDate,
            scheduledReminders: request.ScheduledReminders);

        if (childResult.IsFailure)
            return Result.Failure<Guid>(childResult.Error);

        var child = childResult.Value;

        if (request.TagIds is { Count: > 0 })
        {
            var tags = await tagRepository.FindTrackedAsync(
                t => request.TagIds.Contains(t.Id) && t.UserId == request.UserId,
                cancellationToken);
            foreach (var tag in tags)
                child.AddTag(tag);
        }

        await habitRepository.AddAsync(child, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(childResult.Value.Id);
    }

    private static async Task<int> GetDepthAsync(Habit habit, IGenericRepository<Habit> repo, CancellationToken ct)
    {
        var depth = 0;
        var current = habit;
        while (current.ParentHabitId is not null)
        {
            depth++;
            current = await repo.GetByIdAsync(current.ParentHabitId.Value, ct);
            if (current is null) break;
        }
        return depth;
    }

}
