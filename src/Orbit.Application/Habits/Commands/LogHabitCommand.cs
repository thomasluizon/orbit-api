using System.Data.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Services;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

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
    DateOnly? Date = null) : IRequest<Result<LogHabitResponse>>;

/// <summary>
/// Groups repository dependencies for habit logging to reduce constructor parameter count (S107).
/// </summary>
public record LogHabitRepositories(
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<HabitLog> HabitLogRepository,
    IGenericRepository<Goal> GoalRepository,
    IGenericRepository<User> UserRepository);

/// <summary>
/// Groups supporting services for habit logging to reduce constructor parameter count (S107).
/// </summary>
public record LogHabitServices(
    IUserDateService UserDateService,
    IUserStreakService UserStreakService,
    IGamificationService GamificationService,
    IMediator Mediator);

public partial class LogHabitCommandHandler(
    LogHabitRepositories repos,
    LogHabitServices services,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
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

        var today = await services.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var targetDate = request.Date ?? today;

        var dateValidation = ValidateTargetDate(habit, targetDate, today);
        if (dateValidation.IsFailure)
            return Result.Failure<LogHabitResponse>(dateValidation.Error);

        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);

        var existingLog = habit.Logs.FirstOrDefault(l => l.Date == targetDate && l.Value > 0);
        if (existingLog is not null && !habit.IsFlexible && !habit.IsBadHabit)
            return await HandleUnlogAsync(habit, targetDate, today, cancellationToken);

        return await HandleLogAsync(habit, request, targetDate, today, user, cancellationToken);
    }

    private static Result ValidateTargetDate(Habit habit, DateOnly targetDate, DateOnly today)
    {
        if (targetDate > today && habit.FrequencyUnit is not null)
            return Result.Failure("Cannot log a future date.");

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return Result.Failure("Cannot log a date beyond the overdue window.");

        if (habit.FrequencyUnit is not null && !habit.IsFlexible
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
        {
            var isOverdue = targetDate == today && HabitScheduleService.HasMissedPastOccurrence(habit, today);
            if (!isOverdue)
                return Result.Failure("Habit is not scheduled on this date.");
        }

        return Result.Success();
    }

    private async Task<Result<LogHabitResponse>> HandleUnlogAsync(
        Habit habit, DateOnly targetDate, DateOnly today, CancellationToken cancellationToken)
    {
        var unlogResult = habit.Unlog(targetDate);
        if (unlogResult.IsFailure)
            return Result.Failure<LogHabitResponse>(unlogResult.Error);

        repos.HabitLogRepository.Remove(unlogResult.Value);

        var unlogGoalUpdates = await UpdateLinkedGoalProgress(habit, -1, today, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var streakState = await services.UserStreakService.RecalculateAsync(
            habit.UserId, cancellationToken, awardFreezeIfEligible: false);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId);

        return Result.Success(new LogHabitResponse(
            unlogResult.Value.Id,
            IsFirstCompletionToday: false,
            CurrentStreak: streakState?.CurrentStreak ?? 0,
            LinkedGoalUpdates: unlogGoalUpdates));
    }

    private async Task<Result<LogHabitResponse>> HandleLogAsync(
        Habit habit, LogHabitCommand request, DateOnly targetDate, DateOnly today,
        User? user, CancellationToken cancellationToken)
    {
        var isFirstCompletionToday = user is not null
            && !await repos.HabitRepository.AnyAsync(
                h => h.UserId == request.UserId && h.Logs.Any(l => l.Date == targetDate && l.Value > 0),
                cancellationToken);

        var shouldAdvanceDueDate = targetDate >= today;
        var logResult = habit.Log(targetDate, advanceDueDate: shouldAdvanceDueDate);
        if (logResult.IsFailure)
            return Result.Failure<LogHabitResponse>(logResult.Error);

        await repos.HabitLogRepository.AddAsync(logResult.Value, cancellationToken);

        var goalUpdates = await UpdateLinkedGoalProgress(habit, 1, today, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return await BuildAlreadyLoggedResultAsync(habit, targetDate, cancellationToken);
        }

        var streakState = await services.UserStreakService.RecalculateAsync(request.UserId, cancellationToken);
        var gamificationResult = await ProcessGamificationSafeAsync(request.UserId, request.HabitId, cancellationToken);

        if (gamificationResult is null)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId);

        await CheckReferralCompletionSafeAsync(request.UserId, cancellationToken);

        return Result.Success(new LogHabitResponse(
            logResult.Value.Id,
            isFirstCompletionToday,
            CurrentStreak: streakState?.CurrentStreak ?? 0,
            LinkedGoalUpdates: goalUpdates,
            XpEarned: gamificationResult?.XpEarned,
            NewAchievementIds: gamificationResult?.NewAchievementIds));
    }

    private async Task<Result<LogHabitResponse>> BuildAlreadyLoggedResultAsync(
        Habit habit, DateOnly targetDate, CancellationToken cancellationToken)
    {
        var existingLogs = await repos.HabitLogRepository.FindAsync(
            l => l.HabitId == habit.Id && l.Date == targetDate && l.Value > 0, cancellationToken);
        var winningLog = existingLogs.OrderByDescending(l => l.Id).First();

        var users = await repos.UserRepository.FindAsync(u => u.Id == habit.UserId, cancellationToken);
        var currentStreak = users.SingleOrDefault()?.CurrentStreak ?? 0;

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId);

        return Result.Success(new LogHabitResponse(
            winningLog.Id,
            IsFirstCompletionToday: false,
            CurrentStreak: currentStreak));
    }

    private const string PostgresUniqueViolationSqlState = "23505";

    private static bool IsUniqueViolation(Exception exception)
    {
        return exception switch
        {
            DbUpdateException dbUpdateException => IsUniqueViolation(dbUpdateException.InnerException ?? dbUpdateException),
            DbException dbException => dbException.SqlState == PostgresUniqueViolationSqlState,
            _ => false
        };
    }

    private async Task<HabitLogGamificationResult?> ProcessGamificationSafeAsync(
        Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        try
        {
            return await services.GamificationService.ProcessHabitLogged(userId, habitId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGamificationHabitLogFailed(logger, ex, habitId);
            return null;
        }
    }

    private async Task CheckReferralCompletionSafeAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await services.Mediator.Send(new CheckReferralCompletionCommand(userId), cancellationToken);
        }
        catch (Exception ex)
        {
            LogReferralCompletionCheckFailed(logger, ex, userId);
        }
    }

    private async Task<IReadOnlyList<LinkedGoalUpdate>?> UpdateLinkedGoalProgress(Habit habit, decimal delta, DateOnly today, CancellationToken ct)
    {
        if (habit.Goals.Count == 0) return null;

        var goalIds = habit.Goals.Select(g => g.Id).ToHashSet();

        var trackedGoals = await repos.GoalRepository.FindTrackedAsync(
            g => goalIds.Contains(g.Id),
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs),
            ct);

        var updates = new List<LinkedGoalUpdate>();
        foreach (var trackedGoal in trackedGoals)
        {
            if (trackedGoal.Type == GoalType.Streak && trackedGoal.Status == GoalStatus.Active)
            {
                if (GoalStreakSyncService.SyncCurrentStreak(trackedGoal, today))
                {
                    updates.Add(new LinkedGoalUpdate(trackedGoal.Id, trackedGoal.Title, trackedGoal.CurrentValue, trackedGoal.TargetValue));
                }
            }
            else if (trackedGoal.Status == GoalStatus.Active)
            {
                var newValue = Math.Max(0, trackedGoal.CurrentValue + delta);
                trackedGoal.UpdateProgress(newValue);
                updates.Add(new LinkedGoalUpdate(trackedGoal.Id, trackedGoal.Title, newValue, trackedGoal.TargetValue));
            }
        }

        return updates;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for habit {HabitId}")]
    private static partial void LogGamificationHabitLogFailed(ILogger logger, Exception ex, Guid habitId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Referral completion check failed for user {UserId}")]
    private static partial void LogReferralCompletionCheckFailed(ILogger logger, Exception ex, Guid userId);
}
