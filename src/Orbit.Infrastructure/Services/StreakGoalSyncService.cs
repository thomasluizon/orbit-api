using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Goals.Services;
using Orbit.Application.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

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

        Guid? cursor = null;
        var synced = 0;
        var pageSize = configuration.GetValue("BackgroundServices:ScanPageSize", JobPaging.DefaultPageSize);
        while (true)
        {
            var goals = await dbContext.Goals
                .Where(g => g.Status == GoalStatus.Active
                            && !g.IsDeleted
                            && (g.Type == GoalType.Streak
                                || (g.Type == GoalType.Standard && g.Habits.Any())))
                .Page(cursor, pageSize)
                .AsNoTracking()
                .Select(goal => new { goal.Id, goal.UserId }).ToListAsync(ct);

            if (goals.Count == 0) break;
            cursor = goals[^1].Id;
            var ownerIds = goals.Select(goal => goal.UserId).Distinct().ToList();
            var owners = await dbContext.Users.AsNoTracking().Where(user => ownerIds.Contains(user.Id))
                .Select(user => new { user.Id, user.TimeZone, user.WeekStartDay }).ToDictionaryAsync(user => user.Id, ct);
            foreach (var userGoals in goals.GroupBy(g => g.UserId))
            {
                if (!owners.TryGetValue(userGoals.Key, out var user)) continue;
                var userToday = await userDateService.GetUserTodayAsync(user.TimeZone, user.Id, ct);
                var updates = await goalCompletionService.SyncDerivedGoalsAsync(
                    userGoals.Key,
                    userGoals.Select(g => g.Id).ToList(),
                    userToday,
                    passiveSync: true,
                    cancellationToken: ct,
                    preferences: new DerivedGoalPreferences(user.WeekStartDay, TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id)));
                synced += updates.Count;
            }

            dbContext.ChangeTracker.Clear();
            if (goals.Count < pageSize) break;
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
