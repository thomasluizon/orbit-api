using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Time-driven sweep that reconciles active derived goals without waiting for a request. Standard
/// goals may already qualify from historical completion logs when derivation first applies, while a
/// Streak goal may advance as time passes without a request. Each tick recomputes linked Standard
/// goals and every Streak goal, then routes any Active to Completed transition through persistence
/// and gamification exactly once.
/// </summary>
public partial class GoalProgressReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GoalProgressReconciliationService> logger,
    IConfiguration configuration) : BackgroundService, IScheduledJob
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:StreakGoalSyncIntervalMinutes", 60));

    public string Name => "streak-goal-sync";

    public string CronExpression => "0 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await SyncActiveGoals(cancellationToken);
        BackgroundServiceHealthCheck.RecordTick("StreakGoalSync");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SyncActiveGoals(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("StreakGoalSync");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogServiceError(logger, ex);
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        finally
        {
            LogServiceStopped(logger);
        }
    }

    internal async Task SyncActiveGoals(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var goals = await dbContext.Goals
            .Where(g => g.Status == GoalStatus.Active
                        && !g.IsDeleted
                        && (g.Type == GoalType.Streak
                            || (g.Type == GoalType.Standard && g.Habits.Any())))
            .Include(g => g.Habits).ThenInclude(h => h.Logs)
            .ToListAsync(ct);

        if (goals.Count == 0) return;

        var userIds = goals.Select(g => g.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var synced = 0;
        var usersWithCompletedGoal = new HashSet<Guid>();
        foreach (var goal in goals)
        {
            if (!users.TryGetValue(goal.UserId, out var user)) continue;

            var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var userToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));

            var outcome = goal.Type == GoalType.Streak
                ? ToProgressOutcome(GoalStreakSyncService.SyncCurrentStreakIfNeeded(goal, userToday))
                : GoalProgressSyncService.SyncCurrentProgress(goal, userToday);
            if (!outcome.Synced) continue;

            if (!await TrySaveGoalAsync(goal, dbContext, ct)) continue;

            synced++;
            if (outcome.JustCompleted)
                usersWithCompletedGoal.Add(goal.UserId);
        }

        if (usersWithCompletedGoal.Count > 0)
            await ProcessCompletedGoalsAsync(scope.ServiceProvider, usersWithCompletedGoal, ct);

        if (synced > 0 && logger.IsEnabled(LogLevel.Information))
            LogDerivedGoalsSynced(logger, synced);
    }

    private static GoalProgressSyncOutcome ToProgressOutcome(StreakSyncOutcome outcome) =>
        new(outcome.Synced, outcome.JustCompleted);

    private async Task<bool> TrySaveGoalAsync(Goal goal, OrbitDbContext dbContext, CancellationToken ct)
    {
        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || DbUniqueViolation.IsUniqueViolation(ex))
        {
            await dbContext.Entry(goal).ReloadAsync(ct);
            if (logger.IsEnabled(LogLevel.Debug))
                LogGoalProgressSyncConflict(logger, goal.Id);
            return false;
        }
    }

    private async Task ProcessCompletedGoalsAsync(
        IServiceProvider scopedProvider, HashSet<Guid> userIds, CancellationToken ct)
    {
        var gamificationService = scopedProvider.GetRequiredService<IGamificationService>();
        foreach (var userId in userIds)
        {
            try
            {
                await gamificationService.ProcessGoalCompleted(userId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogGamificationGoalCompletionFailed(logger, ex, userId);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "GoalProgressReconciliationService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "GoalProgressReconciliationService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in derived goal reconciliation")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Synced {Count} active derived goals")]
    private static partial void LogDerivedGoalsSynced(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Gamification processing failed for derived goal completion by user {UserId}")]
    private static partial void LogGamificationGoalCompletionFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Goal {GoalId} progress sync raced a concurrent writer; skipping")]
    private static partial void LogGoalProgressSyncConflict(ILogger logger, Guid goalId);
}
