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

namespace Orbit.Application.Habits.Commands;

public record LinkedGoalUpdate(Guid GoalId, string Title, decimal NewProgress, decimal TargetValue);

public record LogHabitResponse(
    Guid LogId,
    bool IsFirstCompletionToday,
    int CurrentStreak,
    IReadOnlyList<LinkedGoalUpdate>? LinkedGoalUpdates = null,
    int? XpEarned = null,
    IReadOnlyList<string>? NewAchievementIds = null);

public record LogHabitCommand(
    Guid UserId,
    Guid HabitId,
    string? Note = null,
    DateOnly? Date = null) : IRequest<Result<LogHabitResponse>>;

/// <summary>
/// Groups repository dependencies for habit logging to reduce constructor parameter count (S107).
/// </summary>
public record LogHabitRepositories(
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<HabitLog> HabitLogRepository,
    IGenericRepository<Goal> GoalRepository,
    IGenericRepository<User> UserRepository);

public partial class LogHabitCommandHandler(
    LogHabitRepositories repos,
    IUserDateService userDateService,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    IMediator mediator,
    ILogger<LogHabitCommandHandler> logger) : IRequestHandler<LogHabitCommand, Result<LogHabitResponse>>
{
    public async Task<Result<LogHabitResponse>> Handle(LogHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await repos.HabitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs).Include(h => h.Goals),
            cancellationToken);

        if (habit is null)
            return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotOwned, ErrorCodes.HabitNotOwned);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var targetDate = request.Date ?? today;

        // Validate target date (one-time tasks can be completed early)
        if (targetDate > today && habit.FrequencyUnit is not null)
            return Result.Failure<LogHabitResponse>("Cannot log a future date.");

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return Result.Failure<LogHabitResponse>("Cannot log a date beyond the overdue window.");

        // Validate the habit is actually scheduled on the target date (for recurring habits)
        // Allow logging on today if the habit is overdue (has a missed past occurrence)
        if (habit.FrequencyUnit is not null && !habit.IsFlexible
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
        {
            var isOverdue = targetDate == today && HabitScheduleService.HasMissedPastOccurrence(habit, today);
            if (!isOverdue)
                return Result.Failure<LogHabitResponse>("Habit is not scheduled on this date.");
        }

        // Load user for streak info
        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);

        // Toggle: if already logged for the target date, unlog it (skip for flexible/bad habits which allow multiple logs)
        // Only match completion logs (Value > 0) to prevent toggle from removing skip logs (Value == 0)
        var existingLog = habit.Logs.FirstOrDefault(l => l.Date == targetDate && l.Value > 0);
        if (existingLog is not null && !habit.IsFlexible && !habit.IsBadHabit)
        {
            var unlogResult = habit.Unlog(targetDate);
            if (unlogResult.IsFailure)
                return Result.Failure<LogHabitResponse>(unlogResult.Error);

            repos.HabitLogRepository.Remove(unlogResult.Value);

            // Decrement linked goal progress
            var unlogGoalUpdates = await UpdateLinkedGoalProgress(habit, -1, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

            return Result.Success(new LogHabitResponse(
                unlogResult.Value.Id,
                IsFirstCompletionToday: false,
                CurrentStreak: user?.CurrentStreak ?? 0,
                LinkedGoalUpdates: unlogGoalUpdates));
        }

        // Check if this is the first completion today (before creating the log)
        var isFirstCompletionToday = false;
        if (user is not null)
        {
            // Use AnyAsync for efficient EXISTS query instead of loading full entities
            isFirstCompletionToday = !await repos.HabitRepository.AnyAsync(
                h => h.UserId == request.UserId && h.Logs.Any(l => l.Date == targetDate && l.Value > 0),
                cancellationToken);
        }

        // Only advance DueDate when logging today or future-adjacent (not past overdue instances)
        var shouldAdvanceDueDate = targetDate >= today;
        var logResult = habit.Log(targetDate, request.Note, advanceDueDate: shouldAdvanceDueDate);

        if (logResult.IsFailure)
            return Result.Failure<LogHabitResponse>(logResult.Error);

        await repos.HabitLogRepository.AddAsync(logResult.Value, cancellationToken);

        // Increment linked goal progress
        var goalUpdates = await UpdateLinkedGoalProgress(habit, 1, cancellationToken);

        // Update user streak
        if (user is not null)
            user.UpdateStreak(targetDate);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Gamification: process habit completion and capture results
        HabitLogGamificationResult? gamificationResult = null;
        try
        {
            gamificationResult = await gamificationService.ProcessHabitLogged(request.UserId, request.HabitId, cancellationToken);
        }
        catch (Exception ex) { LogGamificationHabitLogFailed(logger, ex, request.HabitId); }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

        // Check referral completion (fire and forget - don't fail the log)
        try
        {
            await mediator.Send(new CheckReferralCompletionCommand(request.UserId), cancellationToken);
        }
        catch (Exception ex)
        {
            LogReferralCompletionCheckFailed(logger, ex, request.UserId);
        }

        return Result.Success(new LogHabitResponse(
            logResult.Value.Id,
            isFirstCompletionToday,
            CurrentStreak: user?.CurrentStreak ?? 0,
            LinkedGoalUpdates: goalUpdates,
            XpEarned: gamificationResult?.XpEarned,
            NewAchievementIds: gamificationResult?.NewAchievementIds));
    }

    private async Task<IReadOnlyList<LinkedGoalUpdate>?> UpdateLinkedGoalProgress(Habit habit, decimal delta, CancellationToken ct)
    {
        if (habit.Goals.Count == 0) return null;

        var goalIds = habit.Goals.Select(g => g.Id).ToHashSet();

        // Load all linked goals in a single query instead of one per goal
        var trackedGoals = await repos.GoalRepository.FindTrackedAsync(
            g => goalIds.Contains(g.Id), ct);

        var updates = new List<LinkedGoalUpdate>();
        foreach (var trackedGoal in trackedGoals)
        {
            var newValue = Math.Max(0, trackedGoal.CurrentValue + delta);
            trackedGoal.UpdateProgress(newValue);
            updates.Add(new LinkedGoalUpdate(trackedGoal.Id, trackedGoal.Title, newValue, trackedGoal.TargetValue));
        }

        return updates;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for habit {HabitId}")]
    private static partial void LogGamificationHabitLogFailed(ILogger logger, Exception ex, Guid habitId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Referral completion check failed for user {UserId}")]
    private static partial void LogReferralCompletionCheckFailed(ILogger logger, Exception ex, Guid userId);
}
