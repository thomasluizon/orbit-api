using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Daily background service that purges soft-deleted entities once they fall outside the
/// incremental-sync window. The cutoff carries a one-day margin beyond the window a client
/// is allowed to request (<see cref="AppConstants.MaxSyncWindowDays"/>) so a row is never
/// purged on the exact boundary a slow client may still request it via /sync/changes.
/// </summary>
public partial class SyncCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncCleanupService> logger) : BackgroundService, IScheduledJob
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetentionPeriod =
        TimeSpan.FromDays(AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays);
    private static readonly TimeSpan SuggestionRetentionPeriod = TimeSpan.FromDays(14);

    public string Name => "sync-cleanup";

    public string CronExpression => "30 3 * * *";

    public Task RunAsync(CancellationToken cancellationToken) => PurgeSoftDeletedEntities(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PurgeSoftDeletedEntities(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("SyncCleanup");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogServiceError(logger, ex);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        finally
        {
            LogServiceStopped(logger);
        }
    }

    internal async Task PurgeSoftDeletedEntities(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var cutoff = DateTime.UtcNow - RetentionPeriod;
        var totalPurged = 0;

        var deletedHabits = await dbContext.Habits
            .IgnoreQueryFilters()
            .Where(h => h.IsDeleted && h.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedHabits.Count > 0)
        {
            dbContext.Habits.RemoveRange(deletedHabits);
            totalPurged += deletedHabits.Count;
        }

        var deletedGoals = await dbContext.Goals
            .IgnoreQueryFilters()
            .Where(g => g.IsDeleted && g.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedGoals.Count > 0)
        {
            dbContext.Goals.RemoveRange(deletedGoals);
            totalPurged += deletedGoals.Count;
        }

        var deletedTags = await dbContext.Tags
            .IgnoreQueryFilters()
            .Where(t => t.IsDeleted && t.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedTags.Count > 0)
        {
            dbContext.Tags.RemoveRange(deletedTags);
            totalPurged += deletedTags.Count;
        }

        var deletedFacts = await dbContext.UserFacts
            .IgnoreQueryFilters()
            .Where(f => f.IsDeleted && f.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedFacts.Count > 0)
        {
            dbContext.UserFacts.RemoveRange(deletedFacts);
            totalPurged += deletedFacts.Count;
        }

        var deletedHabitLogs = await dbContext.HabitLogs
            .IgnoreQueryFilters()
            .Where(l => l.IsDeleted && l.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedHabitLogs.Count > 0)
        {
            dbContext.HabitLogs.RemoveRange(deletedHabitLogs);
            totalPurged += deletedHabitLogs.Count;
        }

        var deletedGoalProgressLogs = await dbContext.GoalProgressLogs
            .IgnoreQueryFilters()
            .Where(l => l.IsDeleted && l.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedGoalProgressLogs.Count > 0)
        {
            dbContext.GoalProgressLogs.RemoveRange(deletedGoalProgressLogs);
            totalPurged += deletedGoalProgressLogs.Count;
        }

        var deletedNotifications = await dbContext.Notifications
            .IgnoreQueryFilters()
            .Where(n => n.IsDeleted && n.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedNotifications.Count > 0)
        {
            dbContext.Notifications.RemoveRange(deletedNotifications);
            totalPurged += deletedNotifications.Count;
        }

        var deletedChecklistTemplates = await dbContext.ChecklistTemplates
            .IgnoreQueryFilters()
            .Where(ct2 => ct2.IsDeleted && ct2.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedChecklistTemplates.Count > 0)
        {
            dbContext.ChecklistTemplates.RemoveRange(deletedChecklistTemplates);
            totalPurged += deletedChecklistTemplates.Count;
        }

        var suggestionCutoff = DateTime.UtcNow - SuggestionRetentionPeriod;
        var abandonedSuggestions = await dbContext.GoogleCalendarSyncSuggestions
            .Where(s => s.DiscoveredAtUtc < suggestionCutoff && s.ImportedAtUtc == null)
            .ToListAsync(ct);

        if (abandonedSuggestions.Count > 0)
        {
            dbContext.GoogleCalendarSyncSuggestions.RemoveRange(abandonedSuggestions);
            totalPurged += abandonedSuggestions.Count;
        }

        if (totalPurged > 0)
        {
            await dbContext.SaveChangesAsync(ct);
            LogEntitiesPurged(logger, totalPurged);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "SyncCleanupService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "SyncCleanupService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in sync cleanup")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Purged {Count} soft-deleted entities outside the sync window")]
    private static partial void LogEntitiesPurged(ILogger logger, int count);
}
