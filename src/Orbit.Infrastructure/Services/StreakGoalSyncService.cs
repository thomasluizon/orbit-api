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
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Time-driven sweep that advances active Streak goals without waiting for a request.
/// A Streak goal's progress only changes when its linked habits' logs are recomputed, and every
/// other trigger is request-bound (the three goal read queries, habit log/skip). A user who simply
/// keeps resisting a bad habit issues no request, so without this sweep their streak goal stays at
/// its last-synced value and never auto-completes. Each tick recomputes every active streak goal in
/// the goal owner's local timezone and routes any Active to Completed transition through gamification
/// exactly once. Distinct from the static <see cref="GoalStreakSyncService"/>, which is the pure
/// per-goal sync helper this service invokes.
/// </summary>
public partial class StreakGoalSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<StreakGoalSyncService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:StreakGoalSyncIntervalMinutes", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SyncActiveStreakGoals(stoppingToken);
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

    internal async Task SyncActiveStreakGoals(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var streakWindowStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-AppConstants.MaxStreakLookbackDays - 1);
        var goals = await dbContext.Goals
            .Where(g => g.Type == GoalType.Streak && g.Status == GoalStatus.Active && !g.IsDeleted)
            .Include(g => g.Habits).ThenInclude(h => h.Logs.Where(l => l.Date >= streakWindowStart))
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

            var outcome = GoalStreakSyncService.SyncCurrentStreakIfNeeded(goal, userToday);
            if (!outcome.Synced) continue;

            if (!await TrySaveGoalAsync(goal, dbContext, ct)) continue;

            synced++;
            if (outcome.JustCompleted)
                usersWithCompletedGoal.Add(goal.UserId);
        }

        if (usersWithCompletedGoal.Count > 0)
            await ProcessCompletedGoalsAsync(scope.ServiceProvider, usersWithCompletedGoal, ct);

        if (synced > 0 && logger.IsEnabled(LogLevel.Information))
            LogStreakGoalsSynced(logger, synced);
    }

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
            if (logger.IsEnabled(LogLevel.Information))
                LogStreakGoalSyncConflict(logger, goal.Id);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "StreakGoalSyncService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "StreakGoalSyncService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in streak goal sync")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Synced {Count} active streak goals")]
    private static partial void LogStreakGoalsSynced(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Gamification processing failed for streak goal completion by user {UserId}")]
    private static partial void LogGamificationGoalCompletionFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Streak goal {GoalId} sync raced a concurrent writer; skipping (already synced)")]
    private static partial void LogStreakGoalSyncConflict(ILogger logger, Guid goalId);
}
