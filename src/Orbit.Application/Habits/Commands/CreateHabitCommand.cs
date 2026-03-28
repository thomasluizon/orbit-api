using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

using Orbit.Application.Common.Attributes;

namespace Orbit.Application.Habits.Commands;

[AiAction(
    "CreateHabit",
    """**Create and track habits** (e.g., "I want to meditate daily", "I want to run 5km every week")""",
    """
    - User explicitly tells you what to create with clear details: "create a daily running habit", "add morning routine with meditate, journal, stretch"
    - It's a simple one-time task: "buy eggs today"
    - Use CreateHabit with subHabits when user explicitly lists what the sub-habits should be
    """,
    DisplayOrder = 10)]
[AiRule("ALWAYS include dueDate when creating habits")]
[AiRule("Omit frequencyUnit and frequencyQuantity for one-time tasks")]
[AiRule("days CANNOT be set if frequencyQuantity > 1")]
[AiRule("Only include tagNames if user explicitly mentioned tagging it")]
[AiRule("BAD HABITS: Set isBadHabit to true for habits the user wants to AVOID or STOP doing. Bad habits track slip-ups/occurrences (smoking, nail biting, etc.)")]
[AiRule("When logging habits, include a note if the user provides context or feelings about the activity")]
[AiRule("TAGS: You can assign tags to habits using tagNames on CreateHabit actions, or use AssignTags action to change tags on existing habits. tagNames is an array of tag name strings. Use EXISTING tag names from the user's tags list when possible. If user asks for a tag that doesn't exist yet, use the new name - it will be auto-created")]
[AiRule("FREQUENCY INTERPRETATION: 'X times per week' (e.g., '3x per week', '3 vezes por semana') WITHOUT specifying days means isFlexible=true, frequencyUnit: Week, frequencyQuantity: X. If user specifies days, use frequencyUnit: Day, frequencyQuantity: 1, days: [...]. Example: '3x/week' (no specific days) = isFlexible=true, Week/3. '3x/week on Mon/Wed/Fri' = Day/1/[Monday,Wednesday,Friday]")]
[AiRule("ONLY add/change tags when the user EXPLICITLY asks for it. NEVER auto-assign tags on your own initiative")]
[AiRule("GENERAL HABITS: Set isGeneral to true for open-ended habits with no specific schedule or due date (e.g., 'drink more water', 'be more mindful', 'read more books'). Omit frequencyUnit, frequencyQuantity, days, and dueDate when isGeneral is true")]
[AiRule("END DATE: endDate is only for recurring habits, not one-time tasks. Use when user specifies a limited time period (e.g., 'daily for 30 days', 'until March 15th')")]
[AiExample(
    "Clean the house 3x per week",
    """{ "actions": [{ "type": "CreateHabit", "title": "Clean the House", "frequencyUnit": "Week", "frequencyQuantity": 3, "isFlexible": true, "dueDate": "{TODAY}" }], "aiMessage": "Created 'Clean the House' - 3 times per week, any days you choose!" }""",
    Note = "X times per week without specific days = flexible")]
[AiExample(
    "I want to meditate on weekdays",
    """{ "actions": [{ "type": "CreateHabit", "title": "Meditation", "frequencyUnit": "Day", "frequencyQuantity": 1, "days": ["Monday","Tuesday","Wednesday","Thursday","Friday"], "dueDate": "{TODAY}" }], "aiMessage": "Created a weekday meditation habit!" }""")]
[AiExample(
    "Create workout plan with gym MWF and cardio TuTh",
    """{ "actions": [{ "type": "CreateHabit", "title": "Workout Plan", "frequencyUnit": "Day", "frequencyQuantity": 1, "subHabits": [{ "title": "Gym", "days": ["Monday","Wednesday","Friday"] }, { "title": "Cardio", "days": ["Tuesday","Thursday"] }], "dueDate": "{TODAY}" }], "aiMessage": "Created your Workout Plan!" }""",
    Note = "with subHabits")]
[AiExample(
    "I want to stop smoking",
    """{ "actions": [{ "type": "CreateHabit", "title": "Smoking", "frequencyUnit": "Day", "frequencyQuantity": 1, "isBadHabit": true, "slipAlertEnabled": true, "dueDate": "{TODAY}" }], "aiMessage": "Tracking smoking as a bad habit with slip alerts enabled. Log each slip-up and I'll send you motivational nudges before your usual slip times!" }""",
    Note = "bad habit")]
[AiExample(
    "Buy eggs tomorrow",
    """{ "actions": [{ "type": "CreateHabit", "title": "Buy Eggs", "dueDate": "{TOMORROW}" }], "aiMessage": "Got it, buy eggs tomorrow!" }""",
    Note = "one-time task")]
[AiExample(
    "Dentist appointment tomorrow at 3pm",
    """{ "actions": [{ "type": "CreateHabit", "title": "Dentist Appointment", "dueDate": "{TOMORROW}", "dueTime": "15:00" }], "aiMessage": "Scheduled your dentist appointment for tomorrow at 3pm!" }""",
    Note = "with time")]
[AiExample(
    "Exam tomorrow at 5pm, remind me 30 minutes before",
    """{ "actions": [{ "type": "CreateHabit", "title": "Exam", "dueDate": "{TOMORROW}", "dueTime": "17:00", "reminderEnabled": true, "reminderTimes": [30] }], "aiMessage": "Scheduled your exam for tomorrow at 5pm with a reminder 30 minutes before!" }""",
    Note = "with reminder")]
[AiExample(
    "Create a supermarket list with milk, eggs, bread",
    """{ "actions": [{ "type": "CreateHabit", "title": "Supermarket", "dueDate": "{TODAY}", "checklistItems": [{"text": "Milk", "isChecked": false}, {"text": "Eggs", "isChecked": false}, {"text": "Bread", "isChecked": false}] }], "aiMessage": "Created your supermarket list with 3 items!" }""",
    Note = "with checklist")]
[AiExample(
    "I want to drink more water",
    """{ "actions": [{ "type": "CreateHabit", "title": "Drink More Water", "isGeneral": true }], "aiMessage": "Created a general habit to drink more water!" }""",
    Note = "general habit, no schedule")]
public record CreateHabitCommand(
    Guid UserId,
    [property: AiField("string", "Name of the habit", Required = true)] string Title,
    [property: AiField("string", "Optional description")] string? Description,
    [property: AiField("Day|Week|Month|Year", "OMIT for one-time tasks")] FrequencyUnit? FrequencyUnit,
    [property: AiField("integer", "Defaults to 1. OMIT for one-time tasks")] int? FrequencyQuantity,
    [property: AiField("string[]", "Specific weekdays, only when frequencyQuantity is 1")] IReadOnlyList<System.DayOfWeek>? Days = null,
    [property: AiField("boolean", "True for habits the user wants to AVOID or STOP doing")] bool IsBadHabit = false,
    [property: AiField("object[]", "Array of sub-habit OBJECTS, each with: title (REQUIRED), plus optional frequencyUnit, frequencyQuantity, days, dueDate, description, isBadHabit. Sub-habits INHERIT parent frequency/dueDate when those fields are omitted.")] IReadOnlyList<string>? SubHabits = null,
    [property: AiField("string", "YYYY-MM-DD, when the habit starts or is due", Required = true)] DateOnly? DueDate = null,
    [property: AiField("string", "HH:mm 24h format, e.g. \"15:00\" for 3pm. ONLY include when user mentions a specific time")] TimeOnly? DueTime = null,
    [property: AiField("string", "HH:mm 24h format end time, e.g. \"16:00\". ONLY include when user mentions a time range")] TimeOnly? DueEndTime = null,
    [property: AiField("boolean", "Set true when user asks for a reminder/notification")] bool ReminderEnabled = false,
    [property: AiField("integer[]", "Array of minutes before dueTime to send reminders, e.g. [1440, 30] for 1 day and 30 min before. Default [15]")] IReadOnlyList<int>? ReminderTimes = null,
    [property: AiField("boolean", "Defaults to true when isBadHabit is true -- sends AI-generated motivational alerts before predicted slip windows")] bool SlipAlertEnabled = false,
    [property: AiField("string[]", "Array of tag name strings, ONLY when user explicitly asks to tag it", Name = "tagNames")] IReadOnlyList<Guid>? TagIds = null,
    [property: AiField("object[]", "Array of {text, isChecked} for inline checklists, e.g. shopping lists, packing lists. Use INSTEAD of sub-habits when user wants a simple checklist within a habit")] IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    [property: AiField("boolean", "True for general habits with no schedule or due date (open-ended goals)")] bool IsGeneral = false,
    [property: AiField("string", "YYYY-MM-DD, optional end date. Habit stops appearing after this date. Only for recurring habits, not one-time tasks")] DateOnly? EndDate = null,
    [property: AiField("boolean", "True for flexible frequency habits. When user says 'X times per week' without specifying days, set isFlexible=true, frequencyUnit to the period, frequencyQuantity to X")] bool IsFlexible = false,
    IReadOnlyList<Guid>? GoalIds = null) : IRequest<Result<Guid>>;

public class CreateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IPayGateService payGate,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<CreateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)
    {
        // Check habit limit
        var gateCheck = await payGate.CanCreateHabits(request.UserId, 1, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

        // Check sub-habit access if creating with sub-habits
        if (request.SubHabits is { Count: > 0 })
        {
            var subGateCheck = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
            if (subGateCheck.IsFailure)
                return subGateCheck.PropagateError<Guid>();
        }

        var dueDate = request.DueDate ?? await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var habitResult = Habit.Create(
            request.UserId,
            request.Title,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Description,
            request.Days,
            request.IsBadHabit,
            dueDate,
            dueTime: request.DueTime,
            dueEndTime: request.DueEndTime,
            reminderEnabled: request.ReminderEnabled,
            reminderTimes: request.ReminderTimes,
            slipAlertEnabled: request.SlipAlertEnabled,
            checklistItems: request.ChecklistItems,
            isGeneral: request.IsGeneral,
            isFlexible: request.IsFlexible);

        if (habitResult.IsFailure)
            return Result.Failure<Guid>(habitResult.Error);

        var habit = habitResult.Value;

        if (request.SubHabits is { Count: > 0 })
        {
            foreach (var subTitle in request.SubHabits)
            {
                var childResult = Habit.Create(
                    request.UserId,
                    subTitle,
                    request.FrequencyUnit,
                    request.FrequencyQuantity,
                    dueDate: request.DueDate ?? dueDate,
                    parentHabitId: habit.Id,
                    isGeneral: request.IsGeneral,
                    endDate: request.EndDate);

                if (childResult.IsFailure)
                    return Result.Failure<Guid>(childResult.Error);

                await habitRepository.AddAsync(childResult.Value, cancellationToken);
            }
        }

        if (request.TagIds is { Count: > 0 })
        {
            var tags = await tagRepository.FindTrackedAsync(
                t => request.TagIds.Contains(t.Id) && t.UserId == request.UserId,
                cancellationToken);
            foreach (var tag in tags)
                habit.AddTag(tag);
        }

        if (request.GoalIds is { Count: > 0 })
        {
            if (request.GoalIds.Count > AppConstants.MaxGoalsPerHabit)
                return Result.Failure<Guid>($"A habit can have at most {AppConstants.MaxGoalsPerHabit} linked goals.");

            var goals = await goalRepository.FindTrackedAsync(
                g => request.GoalIds.Contains(g.Id) && g.UserId == request.UserId,
                cancellationToken);
            foreach (var goal in goals)
                habit.AddGoal(goal);
        }

        await habitRepository.AddAsync(habit, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Gamification: process habit creation
        try
        {
            await gamificationService.ProcessHabitCreated(request.UserId, cancellationToken);
        }
        catch { /* gamification failure should not block habit creation */ }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(habit.Id);
    }
}
