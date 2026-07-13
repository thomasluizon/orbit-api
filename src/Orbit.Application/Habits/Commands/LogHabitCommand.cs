using System.Data.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Challenges.Services;
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
    DateOnly? Date = null) : IRequest<Result<LogHabitResponse>>, IIdempotentCommand;

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
    IChallengeProgressService ChallengeProgressService,
    IMediator Mediator);

public partial class LogHabitCommandHandler(
    LogHabitRepositories repos,
    LogHabitServices services,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<LogHabitCommandHandler> logger) : IRequestHandler<LogHabitCommand, Result<LogHabitResponse>>
{
    private const int MaxLogAttempts = 3;

    public async Task<Result<LogHabitResponse>> Handle(LogHabitCommand request, CancellationToken cancellationToken)
    {
        var today = await services.UserDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var habit = await LoadLoggableHabitAsync(request.HabitId, today, cancellationToken);

        if (habit is null)
            return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotOwned);

        var targetDate = request.Date ?? today;

        var dateValidation = ValidateTargetDate(habit, targetDate, today);
        if (dateValidation.IsFailure)
            return dateValidation.PropagateError<LogHabitResponse>();

        var user = await repos.UserRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);

        var existingLog = habit.Logs.FirstOrDefault(l => l.Date == targetDate && l.Value > 0);
        if (existingLog is not null && !habit.IsFlexible && !habit.IsBadHabit)
            return await HandleUnlogAsync(habit, targetDate, today, cancellationToken);

        return await HandleLogAsync(habit, request, targetDate, today, user, cancellationToken);
    }

    private static Result ValidateTargetDate(Habit habit, DateOnly targetDate, DateOnly today)
    {
        if (targetDate > today && habit.FrequencyUnit is not null)
            return Result.Failure(ErrorMessages.CannotLogFutureDate);

        if (targetDate < today.AddDays(-AppConstants.DefaultOverdueWindowDays))
            return Result.Failure(ErrorMessages.BeyondOverdueWindow);

        if (habit.FrequencyUnit is not null && !habit.IsFlexible
            && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
        {
            var isOverdue = targetDate == today && HabitScheduleService.HasMissedPastOccurrence(habit, today);
            if (!isOverdue)
                return Result.Failure(ErrorMessages.NotScheduledOnDate);
        }

        return Result.Success();
    }

    private async Task<Result<LogHabitResponse>> HandleUnlogAsync(
        Habit habit, DateOnly targetDate, DateOnly today, CancellationToken cancellationToken)
    {
        HabitLog unlogEntity;
        LinkedGoalSyncResult goalSync;
        for (var attempt = 1; ; attempt++)
        {
            var unlogResult = habit.Unlog(targetDate);
            if (unlogResult.IsFailure)
                return unlogResult.PropagateError<LogHabitResponse>();
            unlogEntity = unlogResult.Value;

            goalSync = await UpdateLinkedGoalProgress(habit, -1, today, cancellationToken);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxLogAttempts)
            {
                unitOfWork.ResetTracking();
                var reloaded = await LoadLoggableHabitAsync(habit.Id, today, cancellationToken);
                if (reloaded is null)
                    return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotFound);
                habit = reloaded;
            }
        }

        UserStreakState? streakState = null;
        await ConcurrencyRetry.SaveWithRetryAsync(
            unitOfWork,
            async ct => streakState = await services.UserStreakService.RecalculateAsync(
                habit.UserId, ct, awardFreezeIfEligible: false),
            cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId, today);

        return Result.Success(new LogHabitResponse(
            unlogEntity.Id,
            IsFirstCompletionToday: false,
            CurrentStreak: streakState?.CurrentStreak ?? 0,
            LinkedGoalUpdates: goalSync.Updates));
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
        HabitLog logEntity;
        LinkedGoalSyncResult goalSync;
        for (var attempt = 1; ; attempt++)
        {
            var logResult = habit.Log(targetDate, advanceDueDate: shouldAdvanceDueDate);
            if (logResult.IsFailure)
                return logResult.PropagateError<LogHabitResponse>();
            logEntity = logResult.Value;

            await repos.HabitLogRepository.AddAsync(logEntity, cancellationToken);

            goalSync = await UpdateLinkedGoalProgress(habit, 1, today, cancellationToken);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                return await BuildAlreadyLoggedResultAsync(habit, targetDate, today, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxLogAttempts)
            {
                unitOfWork.ResetTracking();
                var reloaded = await LoadLoggableHabitAsync(request.HabitId, today, cancellationToken);
                if (reloaded is null)
                    return Result.Failure<LogHabitResponse>(ErrorMessages.HabitNotFound);
                habit = reloaded;
            }
        }

        var streakState = await services.UserStreakService.RecalculateAsync(request.UserId, cancellationToken);
        var gamificationResult = await ProcessGamificationSafeAsync(request.UserId, request.HabitId, cancellationToken);
        await ProcessChallengeProgressSafeAsync(request.UserId, request.HabitId, cancellationToken);
        await ProcessOnboardingChecklistSafeAsync(request.UserId, OnboardingChecklistSignal.HabitLogged, cancellationToken);

        if (goalSync.AnyJustCompleted)
            await ProcessGoalCompletionSafeAsync(request.UserId, cancellationToken);

        if (gamificationResult is null)
            await PersistStreakRecalcAsync(request.UserId, cancellationToken);

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId, today);

        await CheckReferralCompletionSafeAsync(request.UserId, cancellationToken);

        return Result.Success(new LogHabitResponse(
            logEntity.Id,
            isFirstCompletionToday,
            CurrentStreak: streakState?.CurrentStreak ?? 0,
            LinkedGoalUpdates: goalSync.Updates,
            XpEarned: gamificationResult?.XpEarned,
            NewAchievementIds: gamificationResult?.NewAchievementIds));
    }

    private Task<Habit?> LoadLoggableHabitAsync(Guid habitId, DateOnly today, CancellationToken cancellationToken)
    {
        var loggableWindowStart = today.AddDays(-AppConstants.DefaultOverdueWindowDays);
        return repos.HabitRepository.FindOneTrackedAsync(
            h => h.Id == habitId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= loggableWindowStart)).Include(h => h.Goals),
            cancellationToken);
    }

    private async Task PersistStreakRecalcAsync(Guid userId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxLogAttempts)
            {
                unitOfWork.ResetTracking();
                await services.UserStreakService.RecalculateAsync(userId, cancellationToken);
            }
        }
    }

    private async Task<Result<LogHabitResponse>> BuildAlreadyLoggedResultAsync(
        Habit habit, DateOnly targetDate, DateOnly today, CancellationToken cancellationToken)
    {
        var existingLogs = await repos.HabitLogRepository.FindAsync(
            l => l.HabitId == habit.Id && l.Date == targetDate && l.Value > 0, cancellationToken);
        var winningLog = existingLogs.OrderByDescending(l => l.Id).First();

        var users = await repos.UserRepository.FindAsync(u => u.Id == habit.UserId, cancellationToken);
        var currentStreak = users.SingleOrDefault()?.CurrentStreak ?? 0;

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, habit.UserId, today);

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
            DbUpdateException dbUpdateException => dbUpdateException.InnerException is not null
                && IsUniqueViolation(dbUpdateException.InnerException),
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

    private async Task ProcessChallengeProgressSafeAsync(
        Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        try
        {
            await services.ChallengeProgressService.EvaluateOnHabitLoggedAsync(userId, habitId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogChallengeProgressFailed(logger, ex, habitId);
        }
    }

    private async Task ProcessOnboardingChecklistSafeAsync(
        Guid userId, OnboardingChecklistSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            await services.GamificationService.ProcessOnboardingChecklistAsync(userId, signal, cancellationToken);
        }
        catch (Exception ex)
        {
            LogOnboardingChecklistFailed(logger, ex, userId);
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

    private async Task<LinkedGoalSyncResult> UpdateLinkedGoalProgress(Habit habit, decimal delta, DateOnly today, CancellationToken ct)
    {
        if (habit.Goals.Count == 0) return LinkedGoalSyncResult.None;

        var goalIds = habit.Goals.Select(g => g.Id).ToHashSet();
        var streakWindowStart = today.AddDays(-AppConstants.MaxStreakLookbackDays);

        var trackedGoals = await repos.GoalRepository.FindTrackedAsync(
            g => goalIds.Contains(g.Id),
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs.Where(l => l.Date >= streakWindowStart)),
            ct);

        var updates = new List<LinkedGoalUpdate>();
        var anyJustCompleted = false;
        foreach (var trackedGoal in trackedGoals)
        {
            if (trackedGoal.Type == GoalType.Streak && trackedGoal.Status == GoalStatus.Active)
            {
                var outcome = GoalStreakSyncService.SyncCurrentStreak(trackedGoal, today);
                if (outcome.Synced)
                {
                    anyJustCompleted |= outcome.JustCompleted;
                    updates.Add(new LinkedGoalUpdate(trackedGoal.Id, trackedGoal.Title, trackedGoal.CurrentValue, trackedGoal.TargetValue));
                }
            }
            else if (trackedGoal.Status == GoalStatus.Active)
            {
                var newValue = Math.Max(0, trackedGoal.CurrentValue + delta);
                var progressResult = trackedGoal.UpdateProgress(newValue);
                anyJustCompleted |= progressResult.IsSuccess && progressResult.Value;
                updates.Add(new LinkedGoalUpdate(trackedGoal.Id, trackedGoal.Title, newValue, trackedGoal.TargetValue));
            }
        }

        return new LinkedGoalSyncResult(updates, anyJustCompleted);
    }

    private async Task ProcessGoalCompletionSafeAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await services.GamificationService.ProcessGoalCompleted(userId, ct);
        }
        catch (Exception ex)
        {
            LogGamificationGoalCompletionFailed(logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for habit {HabitId}")]
    private static partial void LogGamificationHabitLogFailed(ILogger logger, Exception ex, Guid habitId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Referral completion check failed for user {UserId}")]
    private static partial void LogReferralCompletionCheckFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Gamification processing failed for linked goal completion by user {UserId}")]
    private static partial void LogGamificationGoalCompletionFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Onboarding checklist processing failed for user {UserId}")]
    private static partial void LogOnboardingChecklistFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Challenge progress processing failed for habit {HabitId}")]
    private static partial void LogChallengeProgressFailed(ILogger logger, Exception ex, Guid habitId);
}

internal record LinkedGoalSyncResult(IReadOnlyList<LinkedGoalUpdate>? Updates, bool AnyJustCompleted)
{
    public static readonly LinkedGoalSyncResult None = new(null, false);
}
