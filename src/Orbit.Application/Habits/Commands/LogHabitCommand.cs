using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

using Orbit.Application.Common.Attributes;

namespace Orbit.Application.Habits.Commands;

[AiAction(
    "LogHabit",
    """**Log habit completions** with optional notes (e.g., "I ran today, felt great!")""",
    """
    - User mentions completing an activity that matches an EXISTING habit from the Active Habits list
    - Use the exact habit ID from the list
    - Include a note if the user shares context or feelings about the activity
    """,
    DisplayOrder = 20)]
[AiExample(
    "I ran today, felt great",
    """{ "actions": [{ "type": "LogHabit", "habitId": "abc-123", "note": "felt great" }], "aiMessage": "Logged your run!" }""",
    Note = """Running ID: "abc-123" """)]
public record LogHabitCommand(
    Guid UserId,
    [property: AiField("string", "ID of the habit to log", Required = true)] Guid HabitId,
    [property: AiField("string", "Include if user shares context or feelings")] string? Note = null) : IRequest<Result<Guid>>;

public class LogHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    IMediator mediator) : IRequestHandler<LogHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(LogHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs).Include(h => h.Goals),
            cancellationToken);

        if (habit is null)
            return Result.Failure<Guid>(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure<Guid>(ErrorMessages.HabitNotOwned);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        // Toggle: if already logged for today, unlog it (skip for flexible/bad habits which allow multiple logs)
        var existingLog = habit.Logs.FirstOrDefault(l => l.Date == today);
        if (existingLog is not null && !habit.IsFlexible && !habit.IsBadHabit)
        {
            var unlogResult = habit.Unlog(today);
            if (unlogResult.IsFailure)
                return Result.Failure<Guid>(unlogResult.Error);

            habitLogRepository.Remove(unlogResult.Value);

            // Decrement linked goal progress
            await UpdateLinkedGoalProgress(habit, -1, cancellationToken);

            // If a child was unlogged, also unlog the auto-completed parent
            await TryUnlogParent(habit, today, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

            return Result.Success(unlogResult.Value.Id);
        }

        var logResult = habit.Log(today, request.Note);

        if (logResult.IsFailure)
            return Result.Failure<Guid>(logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, cancellationToken);

        // Increment linked goal progress
        await UpdateLinkedGoalProgress(habit, 1, cancellationToken);

        // Auto-complete parent when all children are done (recursive up the tree)
        await TryAutoCompleteParent(habit, today, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Gamification: process habit completion (fire-and-forget style, don't fail the log)
        try
        {
            await gamificationService.ProcessHabitLogged(request.UserId, request.HabitId, cancellationToken);
        }
        catch { /* gamification failure should not block habit logging */ }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

        // Check referral completion (fire and forget -- don't fail the log)
        try
        {
            await mediator.Send(new CheckReferralCompletionCommand(request.UserId), cancellationToken);
        }
        catch
        {
            // Silently ignore -- referral is secondary to habit logging
        }

        return Result.Success(logResult.Value.Id);
    }

    private async Task TryAutoCompleteParent(Habit child, DateOnly today, CancellationToken ct)
    {
        if (child.ParentHabitId is null) return;

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == child.ParentHabitId.Value,
            q => q.Include(h => h.Logs)
                  .Include(h => h.Children).ThenInclude(c => c.Logs),
            ct);

        if (parent is null || parent.IsCompleted) return;

        // Only auto-log if the parent is actually due today (or overdue)
        if (parent.DueDate > today) return;

        // Check if ALL children are done for today (logged today or permanently completed)
        if (!parent.Children.Any()) return;

        var allChildrenDone = parent.Children.All(c =>
            c.IsCompleted || c.Logs.Any(l => l.Date == today));
        if (!allChildrenDone) return;

        // Auto-log the parent
        var alreadyLogged = parent.Logs.Any(l => l.Date == today);
        if (!alreadyLogged)
        {
            var logResult = parent.Log(today);
            if (logResult.IsSuccess)
                await habitLogRepository.AddAsync(logResult.Value, ct);
        }

        // Recurse up the tree
        await TryAutoCompleteParent(parent, today, ct);
    }

    private async Task UpdateLinkedGoalProgress(Habit habit, decimal delta, CancellationToken ct)
    {
        if (habit.Goals.Count == 0) return;

        foreach (var goal in habit.Goals)
        {
            var trackedGoal = await goalRepository.FindOneTrackedAsync(
                g => g.Id == goal.Id, cancellationToken: ct);

            if (trackedGoal is null) continue;

            var newValue = Math.Max(0, trackedGoal.CurrentValue + delta);
            trackedGoal.UpdateProgress(newValue);
        }
    }

    private async Task TryUnlogParent(Habit child, DateOnly today, CancellationToken ct)
    {
        if (child.ParentHabitId is null) return;

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == child.ParentHabitId.Value,
            q => q.Include(h => h.Logs),
            ct);

        if (parent is null) return;

        // If parent was logged today, unlog it since a child is no longer done
        var parentLog = parent.Logs.FirstOrDefault(l => l.Date == today);
        if (parentLog is null) return;

        var unlogResult = parent.Unlog(today);
        if (unlogResult.IsSuccess)
            habitLogRepository.Remove(unlogResult.Value);

        // Recurse up the tree
        await TryUnlogParent(parent, today, ct);
    }

}
