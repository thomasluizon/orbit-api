using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Common.Attributes;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

[AiAction(
    "SkipHabit",
    """**Skip habits** - advance to next scheduled date without logging completion (e.g., "skip my morning run today", "skip all my habits")""",
    """
    - User says "skip", "pass on", "not today", "dismiss" for a habit
    - Moves the habit's due date to the next occurrence WITHOUT marking it as completed
    - Only works on RECURRING habits (not one-time tasks) that are DUE TODAY or OVERDUE
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
    [property: AiField("string", "ID of the habit to skip", Required = true)] Guid HabitId) : IRequest<Result>;

public class SkipHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<SkipHabitCommand, Result>
{
    public async Task<Result> Handle(SkipHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.HabitNotOwned);

        if (habit.IsCompleted)
            return Result.Failure("Cannot skip a completed habit.");

        if (habit.FrequencyUnit is null)
            return Result.Failure("Cannot skip a one-time task.");

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        // For flexible habits, skip means advance past current window
        // For regular habits, they must be due today or overdue
        if (!habit.IsFlexible && habit.DueDate > today)
            return Result.Failure("Cannot skip a habit that is not yet due.");

        if (habit.IsFlexible)
            habit.AdvanceDueDatePastWindow(today);
        else
            habit.AdvanceDueDate(today);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

        return Result.Success();
    }
}
