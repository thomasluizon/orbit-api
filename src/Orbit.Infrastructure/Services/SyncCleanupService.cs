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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PurgeSoftDeletedEntities(cancellationToken);
        BackgroundServiceHealthCheck.RecordTick("SyncCleanup");
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

        var totalPurged = await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
            var purged = await PurgeCoreAsync(dbContext, ct);
            await transaction.CommitAsync(ct);
            return purged;
        });
        if (totalPurged > 0)
            LogEntitiesPurged(logger, totalPurged);
    }

    private static async Task<int> PurgeCoreAsync(OrbitDbContext dbContext, CancellationToken ct)
    {
#pragma warning disable ORBIT0004 // cutoff removes sync tombstones by elapsed RetentionPeriod against DeletedAtUtc, independently of user calendar dates. https://github.com/thomasluizon/orbit-tickets/issues/230
        var cutoff = DateTime.UtcNow - RetentionPeriod;
#pragma warning restore ORBIT0004
        var totalPurged = 0;

        var purgedCompletions = await dbContext.Database.SqlQuery<PurgedCompletion>($"""
            WITH RECURSIVE "PurgeHabits" ("Id", "UserId", "IsBadHabit") AS (
                SELECT h."Id", h."UserId", h."IsBadHabit"
                FROM "Habits" h
                WHERE h."IsDeleted" = TRUE AND h."DeletedAtUtc" < {cutoff}
                UNION
                SELECT child."Id", child."UserId", child."IsBadHabit"
                FROM "Habits" child
                JOIN "PurgeHabits" parent
                    ON child."ParentHabitId" = parent."Id" AND child."UserId" = parent."UserId"
            )
            SELECT h."UserId" AS "UserId", MAX(l."Date") AS "Date"
            FROM "PurgeHabits" h
            JOIN "HabitLogs" l ON l."HabitId" = h."Id"
            WHERE h."IsBadHabit" = FALSE AND CAST(l."Value" AS NUMERIC) > 0
                AND l."IsDeleted" = FALSE
            GROUP BY h."UserId"
            """)
            .ToListAsync(ct);

        foreach (var completion in purgedCompletions.OrderBy(completion => completion.UserId))
        {
            await dbContext.Users.IgnoreQueryFilters()
                .Where(user => user.Id == completion.UserId
                    && (user.LastPurgedCompletionDate == null || user.LastPurgedCompletionDate < completion.Date))
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.LastPurgedCompletionDate, completion.Date), ct);
        }

        totalPurged += await dbContext.Habits.IgnoreQueryFilters()
            .Where(habit => habit.IsDeleted && habit.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        totalPurged += await dbContext.Goals.IgnoreQueryFilters()
            .Where(goal => goal.IsDeleted && goal.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        totalPurged += await dbContext.Tags.IgnoreQueryFilters()
            .Where(tag => tag.IsDeleted && tag.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        totalPurged += await dbContext.HabitLogs.IgnoreQueryFilters()
            .Where(log => log.IsDeleted && log.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        totalPurged += await dbContext.GoalProgressLogs.IgnoreQueryFilters()
            .Where(log => log.IsDeleted && log.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        totalPurged += await dbContext.Notifications.IgnoreQueryFilters()
            .Where(notification => notification.IsDeleted && notification.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        totalPurged += await dbContext.ChecklistTemplates.IgnoreQueryFilters()
            .Where(template => template.IsDeleted && template.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);

#pragma warning disable ORBIT0004 // suggestionCutoff removes unimported calendar suggestions by DiscoveredAtUtc age after SuggestionRetentionPeriod. https://github.com/thomasluizon/orbit-tickets/issues/230
        var suggestionCutoff = DateTime.UtcNow - SuggestionRetentionPeriod;
#pragma warning restore ORBIT0004
        totalPurged += await dbContext.GoogleCalendarSyncSuggestions
            .Where(suggestion => suggestion.DiscoveredAtUtc < suggestionCutoff && suggestion.ImportedAtUtc == null)
            .ExecuteDeleteAsync(ct);
        return totalPurged;
    }

    private sealed class PurgedCompletion
    {
        public Guid UserId { get; set; }
        public DateOnly Date { get; set; }
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
