using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Goals.Services;
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
public partial class StreakGoalSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<StreakGoalSyncService> logger,
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
        var goalCompletionService = scope.ServiceProvider.GetRequiredService<IGoalCompletionService>();
        var userDateService = scope.ServiceProvider.GetRequiredService<IUserDateService>();

        var goals = await dbContext.Goals
            .Where(g => g.Status == GoalStatus.Active
                        && !g.IsDeleted
                        && (g.Type == GoalType.Streak
                            || (g.Type == GoalType.Standard && g.Habits.Any())))
            .Select(g => new { g.Id, g.UserId })
            .AsNoTracking()
            .ToListAsync(ct);

        if (goals.Count == 0) return;

        var synced = 0;
        foreach (var userGoals in goals.GroupBy(g => g.UserId))
        {
            var userToday = await userDateService.GetUserTodayAsync(userGoals.Key, ct);
            var updates = await goalCompletionService.SyncDerivedGoalsAsync(
                userGoals.Key,
                userGoals.Select(g => g.Id).ToList(),
                userToday,
                passiveSync: true,
                cancellationToken: ct);
            synced += updates.Count;
        }

        if (synced > 0 && logger.IsEnabled(LogLevel.Information))
            LogDerivedGoalsSynced(logger, synced);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "StreakGoalSyncService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "StreakGoalSyncService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in derived goal reconciliation")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Synced {Count} active derived goals")]
    private static partial void LogDerivedGoalsSynced(ILogger logger, int count);

}
