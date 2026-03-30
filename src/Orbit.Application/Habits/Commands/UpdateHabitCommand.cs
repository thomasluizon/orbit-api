using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

using Orbit.Application.Common.Attributes;

namespace Orbit.Application.Habits.Commands;

[AiAction(
    "UpdateHabit",
    """**Update habits** -- change title, frequency, due date, or any property (e.g., "move my gym to tomorrow", "rename running to jogging")""",
    """
    - User asks to change a habit's date, frequency, name, or any property: "move my gym to tomorrow", "change running to weekly"
    - User asks to reschedule: "push all my habits to tomorrow", "change the date of meditation to next Monday"
    - User asks to rename: "rename my running habit to jogging"
    - For BULK updates, return MULTIPLE UpdateHabit actions, one per habit
    - ONLY update fields the user mentions. Omit unchanged fields.
    - IMPORTANT: When user says "move ALL my habits to tomorrow" or "reschedule everything", they mean habits due TODAY and OVERDUE ones only. Do NOT move habits scheduled for future dates. Check each habit's Due date -- only include those where Due <= today's date.
    - **CONFIRM BEFORE BULK CHANGES:** When a request affects 3+ habits (bulk reschedule, bulk delete, bulk update), do NOT execute immediately. Instead, return EMPTY actions and list the affected habits in aiMessage, asking the user to confirm. Only execute after they confirm.
    """,
    DisplayOrder = 30)]
[AiRule("Only include fields that are changing in UpdateHabit actions")]
[AiExample(
    "Move my gym to tomorrow",
    """{ "actions": [{ "type": "UpdateHabit", "habitId": "abc-123", "dueDate": "{TOMORROW}" }], "aiMessage": "Moved Gym to tomorrow!" }""",
    Note = """Gym ID: "abc-123" """)]
public record UpdateHabitCommand(
    Guid UserId,
    [property: AiField("string", "ID of existing habit", Required = true)] Guid HabitId,
    [property: AiField("string", "New title")] string Title,
    [property: AiField("string", "New description")] string? Description,
    [property: AiField("Day|Week|Month|Year", "New frequency unit")] FrequencyUnit? FrequencyUnit,
    [property: AiField("integer", "New frequency quantity")] int? FrequencyQuantity,
    [property: AiField("string[]", "New specific weekdays")] IReadOnlyList<System.DayOfWeek>? Days = null,
    [property: AiField("boolean", "New bad habit status")] bool IsBadHabit = false,
    [property: AiField("string", "New due date YYYY-MM-DD")] DateOnly? DueDate = null,
    [property: AiField("string", "HH:mm 24h format to set or change time")] TimeOnly? DueTime = null,
    [property: AiField("string", "HH:mm 24h format end time")] TimeOnly? DueEndTime = null,
    [property: AiField("boolean", "Enable or disable reminders")] bool? ReminderEnabled = null,
    [property: AiField("integer[]", "Array of minutes before dueTime for reminders")] IReadOnlyList<int>? ReminderTimes = null,
    [property: AiField("boolean", "Enable or disable slip alerts")] bool? SlipAlertEnabled = null,
    [property: AiField("object[]", "New checklist items")] IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    [property: AiField("boolean", "Set to true to make a general habit (no schedule)")] bool? IsGeneral = null,
    [property: AiField("string", "YYYY-MM-DD, optional end date. Set to null to clear. Habit stops appearing after this date")] DateOnly? EndDate = null,
    [property: AiField("boolean", "Set to true to remove the end date")] bool? ClearEndDate = null,
    [property: AiField("boolean", "Set to true for flexible frequency (X times per period without fixed days)")] bool? IsFlexible = null,
    IReadOnlyList<Guid>? GoalIds = null,
    [property: AiField("object[]", "Absolute-time reminders for habits WITHOUT a due time. Array of {when: 'day_before'|'same_day', time: 'HH:mm'}")] IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null) : IRequest<Result>;

public class UpdateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<SentReminder> sentReminderRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<UpdateHabitCommand, Result>
{
    public async Task<Result> Handle(UpdateHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = request.GoalIds is not null
            ? await habitRepository.FindOneTrackedAsync(
                h => h.Id == request.HabitId && h.UserId == request.UserId,
                q => q.Include(h => h.Goals),
                cancellationToken)
            : await habitRepository.FindOneTrackedAsync(
                h => h.Id == request.HabitId && h.UserId == request.UserId,
                cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var result = habit.Update(
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Days,
            request.IsBadHabit,
            request.DueDate,
            dueTime: request.DueTime,
            dueEndTime: request.DueEndTime,
            reminderEnabled: request.ReminderEnabled,
            reminderTimes: request.ReminderTimes,
            slipAlertEnabled: request.SlipAlertEnabled,
            checklistItems: request.ChecklistItems,
            isGeneral: request.IsGeneral,
            isFlexible: request.IsFlexible,
            scheduledReminders: request.ScheduledReminders);

        if (result.IsFailure)
            return result;

        // If dueTime changed, clear today's sent reminders so they re-trigger
        if (request.DueTime.HasValue)
        {
            var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
            var existing = await sentReminderRepository.FindAsync(
                r => r.HabitId == request.HabitId && r.Date == userToday,
                cancellationToken);
            foreach (var r in existing)
                sentReminderRepository.Remove(r);
        }

        // Sync goal links if GoalIds was provided
        if (request.GoalIds is not null)
        {
            if (request.GoalIds.Count > AppConstants.MaxGoalsPerHabit)
                return Result.Failure($"A habit can have at most {AppConstants.MaxGoalsPerHabit} linked goals.");

            foreach (var existingGoal in habit.Goals.ToList())
                habit.RemoveGoal(existingGoal);

            if (request.GoalIds.Count > 0)
            {
                var goals = await goalRepository.FindTrackedAsync(
                    g => request.GoalIds.Contains(g.Id) && g.UserId == request.UserId,
                    cancellationToken);
                foreach (var goal in goals)
                    habit.AddGoal(goal);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success();
    }
}
