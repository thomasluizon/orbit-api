using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

using Orbit.Application.Common.Attributes;

namespace Orbit.Application.Habits.Commands;

public record LogHabitResponse(
    Guid LogId,
    bool IsFirstCompletionToday,
    int CurrentStreak);

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
    [property: AiField("string", "Include if user shares context or feelings")] string? Note = null,
    [property: AiField("string", "ISO date (YYYY-MM-DD) to log for a specific date, e.g. an overdue instance. Defaults to today.")] DateOnly? Date = null) : IRequest<Result<LogHabitResponse>>;

public class LogHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    IMediator mediator,
    ILogger<LogHabitCommandHandler> logger) : IRequestHandler<LogHabitCommand, Result<LogHabitResponse>>
{
    public async Task<Result<LogHabitResponse>> Handle(LogHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs).Include(h => h.Goals),
            cancellationToken);

        if (habit is null)
            return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotOwned);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var targetDate = request.Date ?? today;

        // Validate target date
        if (targetDate > today)
            return Result.Failure<LogHabitResponse>("Cannot log a future date.");

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return Result.Failure<LogHabitResponse>("Cannot log a date beyond the overdue window.");

        // Validate the habit is actually scheduled on the target date (for recurring habits)
        if (habit.FrequencyUnit is not null && !habit.IsFlexible
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
            return Result.Failure<LogHabitResponse>("Habit is not scheduled on this date.");

        // Load user for streak info
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);

        // Toggle: if already logged for the target date, unlog it (skip for flexible/bad habits which allow multiple logs)
        // Only match completion logs (Value > 0) to prevent toggle from removing skip logs (Value == 0)
        var existingLog = habit.Logs.FirstOrDefault(l => l.Date == targetDate && l.Value > 0);
        if (existingLog is not null && !habit.IsFlexible && !habit.IsBadHabit)
        {
            var unlogResult = habit.Unlog(targetDate);
            if (unlogResult.IsFailure)
                return Result.Failure<LogHabitResponse>(unlogResult.Error);

            habitLogRepository.Remove(unlogResult.Value);

            // Decrement linked goal progress
            await UpdateLinkedGoalProgress(habit, -1, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

            return Result.Success(new LogHabitResponse(
                unlogResult.Value.Id,
                IsFirstCompletionToday: false,
                CurrentStreak: user?.CurrentStreak ?? 0));
        }

        // Check if this is the first completion today (before creating the log)
        var isFirstCompletionToday = false;
        if (user is not null)
        {
            // Check all habits for this user to see if any have a completion log for today
            var userHabits = await habitRepository.FindAsync(
                h => h.UserId == request.UserId,
                q => q.Include(h => h.Logs),
                cancellationToken);

            var hasAnyCompletionToday = userHabits.Any(h =>
                h.Logs.Any(l => l.Date == targetDate && l.Value > 0));

            isFirstCompletionToday = !hasAnyCompletionToday;
        }

        // Only advance DueDate when logging today or future-adjacent (not past overdue instances)
        var shouldAdvanceDueDate = targetDate >= today;
        var logResult = habit.Log(targetDate, request.Note, advanceDueDate: shouldAdvanceDueDate);

        if (logResult.IsFailure)
            return Result.Failure<LogHabitResponse>(logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, cancellationToken);

        // Increment linked goal progress
        await UpdateLinkedGoalProgress(habit, 1, cancellationToken);

        // Update user streak
        if (user is not null)
            user.UpdateStreak(targetDate);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Gamification: process habit completion (fire-and-forget style, don't fail the log)
        try
        {
            await gamificationService.ProcessHabitLogged(request.UserId, request.HabitId, cancellationToken);
        }
        catch { /* gamification failure should not block habit logging */ }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

        // Check referral completion (fire and forget - don't fail the log)
        try
        {
            await mediator.Send(new CheckReferralCompletionCommand(request.UserId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Referral completion check failed for user {UserId}", request.UserId);
        }

        return Result.Success(new LogHabitResponse(
            logResult.Value.Id,
            isFirstCompletionToday,
            CurrentStreak: user?.CurrentStreak ?? 0));
    }

    private async Task UpdateLinkedGoalProgress(Habit habit, decimal delta, CancellationToken ct)
    {
        if (habit.Goals.Count == 0) return;

        var goalIds = habit.Goals.Select(g => g.Id).ToHashSet();

        // Load all linked goals in a single query instead of one per goal
        var trackedGoals = await goalRepository.FindTrackedAsync(
            g => goalIds.Contains(g.Id), ct);

        foreach (var trackedGoal in trackedGoals)
        {
            var newValue = Math.Max(0, trackedGoal.CurrentValue + delta);
            trackedGoal.UpdateProgress(newValue);
        }
    }

}
