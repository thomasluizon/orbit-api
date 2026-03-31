using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Common.Attributes;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

[AiAction(
    "SkipHabit",
    """**Skip/postpone habits** - advance to next scheduled date (recurring) or postpone to tomorrow (one-time tasks) without logging completion (e.g., "skip my morning run today", "postpone my task", "skip all my habits")""",
    """
    - User says "skip", "pass on", "not today", "dismiss", "postpone", "defer" for a habit
    - For RECURRING habits: moves the due date to the next scheduled occurrence WITHOUT marking it as completed
    - For ONE-TIME tasks: postpones the due date to tomorrow
    - Works on habits that are DUE TODAY or OVERDUE and not COMPLETED
    - For "skip all" or "skip everything today", return SkipHabit for EVERY habit marked DUE TODAY or OVERDUE that is not COMPLETED
    - For specific habits, use their exact ID
    - Do NOT confuse with LogHabit: skip = didn't do it, just move on; log = actually completed it
    - When user asks to skip a parent habit AND its sub-habits, return SkipHabit for the parent AND each sub-habit separately (each with its own ID)
    """,
    DisplayOrder = 25)]
[AiExample(
    "Skip my morning run today",
    """{ "actions": [{ "type": "SkipHabit", "habitId": "abc-123" }], "aiMessage": "Skipped Morning Run - moved to next scheduled date!" }""",
    Note = """Morning Run ID: "abc-123" """)]
[AiExample(
    "Skip all my habits today",
    """{ "actions": [{ "type": "SkipHabit", "habitId": "abc-123" }, { "type": "SkipHabit", "habitId": "def-456" }, { "type": "SkipHabit", "habitId": "ghi-789" }], "aiMessage": "Skipped 3 habits - all moved to their next scheduled dates!" }""",
    Note = "multiple habits due today")]
public record SkipHabitCommand(
    Guid UserId,
    [property: AiField("string", "ID of the habit to skip", Required = true)] Guid HabitId,
    [property: AiField("string", "ISO date (YYYY-MM-DD) to skip a specific instance. Defaults to today.")] DateOnly? Date = null) : IRequest<Result>;

public class SkipHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<SkipHabitCommand, Result>
{
    public async Task<Result> Handle(SkipHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.HabitNotOwned);

        if (habit.IsCompleted)
            return Result.Failure("Cannot skip a completed habit.");

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        if (habit.FrequencyUnit is null)
        {
            // One-time task: postpone to tomorrow
            habit.DueDate = today.AddDays(1);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);
            return Result.Success();
        }
        var targetDate = request.Date ?? today;

        // Validate target date
        if (targetDate > today)
            return Result.Failure("Cannot skip a future date.");

        // For flexible habits, skip means record a skip log (Value=0) to reduce the period target
        // For regular habits, they must be due on or before the target date
        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return Result.Failure("Cannot skip a habit that is not yet due.");

        // Validate the habit is actually scheduled on the target date (for non-flexible)
        if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
            return Result.Failure("Habit is not scheduled on this date.");

        if (habit.IsFlexible)
        {
            var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs);
            if (remaining <= 0)
                return Result.Failure("All instances for this period have already been completed or skipped.");

            var skipResult = habit.SkipFlexible(targetDate);
            if (skipResult.IsFailure)
                return Result.Failure(skipResult.Error);

            await habitLogRepository.AddAsync(skipResult.Value, cancellationToken);
        }
        else
        {
            habit.AdvanceDueDate(targetDate);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

        return Result.Success();
    }
}
