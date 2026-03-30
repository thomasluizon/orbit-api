using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

using Orbit.Application.Common.Attributes;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Commands;

[AiAction(
    "CreateSubHabit",
    """**Add sub-habits** to existing parent habits (e.g., "add stretching under my Workout Plan")""",
    """
    - User wants to add a sub-habit to an existing parent: "add X under Y", "create X as part of my Y habit"
    - The parent habit MUST already exist in the Active Habits list with a known ID
    - Do NOT use CreateSubHabit when the parent doesn't exist yet -- use CreateHabit with subHabits instead
    - Do NOT use CreateSubHabit for standalone habits -- use CreateHabit
    """,
    DisplayOrder = 50)]
[AiRule("Use CreateSubHabit to add a sub-habit under an EXISTING parent from the Active Habits list. Requires habitId (the parent's exact ID) and title. The sub-habit inherits the parent's frequency and scheduling. Do NOT confuse with CreateHabit+subHabits which creates a NEW parent. If the parent habit doesn't exist yet, use CreateHabit instead.")]
[AiExample(
    "Add stretching under my Workout Plan",
    """{ "actions": [{ "type": "CreateSubHabit", "habitId": "abc-123", "title": "Stretching", "description": "Post-workout stretching routine" }], "aiMessage": "Added Stretching as a sub-habit under Workout Plan!" }""",
    Note = """Workout Plan ID: "abc-123" """)]
public record CreateSubHabitCommand(
    Guid UserId,
    [property: AiField("string", "ID of existing PARENT habit from Active Habits list", Required = true, Name = "habitId")] Guid ParentHabitId,
    [property: AiField("string", "Name of the new sub-habit", Required = true)] string Title,
    [property: AiField("string", "Optional description")] string? Description,
    [property: AiField("Day|Week|Month|Year", "Override parent frequency")] FrequencyUnit? FrequencyUnit = null,
    [property: AiField("integer", "Override parent frequency quantity")] int? FrequencyQuantity = null,
    [property: AiField("string[]", "Specific weekdays, only when frequencyQuantity is 1")] IReadOnlyList<System.DayOfWeek>? Days = null,
    [property: AiField("string", "HH:mm 24h format")] TimeOnly? DueTime = null,
    [property: AiField("string", "HH:mm 24h format end time")] TimeOnly? DueEndTime = null,
    [property: AiField("boolean", "True for habits the user wants to AVOID")] bool IsBadHabit = false,
    [property: AiField("boolean", "Set true when user asks for a reminder")] bool ReminderEnabled = false,
    [property: AiField("integer[]", "Minutes before dueTime to send reminders")] IReadOnlyList<int>? ReminderTimes = null,
    [property: AiField("boolean", "Enable slip alert notifications")] bool SlipAlertEnabled = false,
    [property: AiField("object[]", "Array of {text, isChecked} for inline checklists")] IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    IReadOnlyList<Guid>? TagIds = null,
    DateOnly? EndDate = null,
    [property: AiField("boolean", "True for flexible frequency")] bool IsFlexible = false,
    [property: AiField("string", "YYYY-MM-DD override for due date")] DateOnly? DueDate = null,
    [property: AiField("object[]", "Absolute-time reminders for habits WITHOUT a due time")] IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null) : IRequest<Result<Guid>>;

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
