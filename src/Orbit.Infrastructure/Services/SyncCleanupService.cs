using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Removes soft-deleted entities after the incremental-sync window plus a one-day margin.
/// The margin beyond <see cref="AppConstants.MaxSyncWindowDays"/> retains rows a slow client may
/// still request via /sync/changes.
/// Unused OAuth registrations are removed after seven days.
/// </summary>
public partial class SyncCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncCleanupService> logger, IConfiguration? configuration = null) : BackgroundService, IScheduledJob
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetentionPeriod =
        TimeSpan.FromDays(AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays);
    private static readonly TimeSpan OAuthRegistrationRetentionPeriod = TimeSpan.FromDays(7);
    private static readonly TimeSpan SuggestionRetentionPeriod = TimeSpan.FromDays(14);

    public string Name => "sync-cleanup";

    public string CronExpression => "30 3 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PurgeSoftDeletedEntities(cancellationToken);
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

        var totalPurged = await PurgeCoreAsync(dbContext, ct);
        totalPurged += await ApplicationHistoryRetention.PurgeAsync(dbContext,
            configuration ?? new ConfigurationBuilder().Build(), TimeProvider.System, ct);
        if (totalPurged > 0)
            LogEntitiesPurged(logger, totalPurged);
    }

    private static async Task<int> PurgeCoreAsync(OrbitDbContext dbContext, CancellationToken ct)
    {
#pragma warning disable ORBIT0004 // cutoff removes sync tombstones by elapsed RetentionPeriod against DeletedAtUtc, independently of user calendar dates. https://github.com/thomasluizon/orbit-tickets/issues/230
        var cutoff = DateTime.UtcNow - RetentionPeriod;
#pragma warning restore ORBIT0004
        var totalPurged = 0;

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
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
            , "Completions" AS (
                SELECT h."UserId" AS "UserId", MAX(l."Date") AS "Date"
                FROM "PurgeHabits" h
                JOIN "HabitLogs" l ON l."HabitId" = h."Id"
                WHERE h."IsBadHabit" = FALSE AND CAST(l."Value" AS NUMERIC) > 0
                    AND l."IsDeleted" = FALSE
                GROUP BY h."UserId"
            )
            UPDATE "Users" SET "LastPurgedCompletionDate" = "Completions"."Date"
            FROM "Completions"
            WHERE "Users"."Id" = "Completions"."UserId"
              AND ("Users"."LastPurgedCompletionDate" IS NULL
                   OR "Users"."LastPurgedCompletionDate" < "Completions"."Date")
            """, ct);

        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.Habits.IgnoreQueryFilters().Where(habit => habit.IsDeleted && habit.DeletedAtUtc < cutoff), ct);
        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.Goals.IgnoreQueryFilters().Where(goal => goal.IsDeleted && goal.DeletedAtUtc < cutoff), ct);
        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.Tags.IgnoreQueryFilters().Where(tag => tag.IsDeleted && tag.DeletedAtUtc < cutoff), ct);
        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.HabitLogs.IgnoreQueryFilters().Where(log => log.IsDeleted && log.DeletedAtUtc < cutoff), ct);
        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.GoalProgressLogs.IgnoreQueryFilters().Where(log => log.IsDeleted && log.DeletedAtUtc < cutoff), ct);
        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.Notifications.IgnoreQueryFilters().Where(notification => notification.IsDeleted && notification.DeletedAtUtc < cutoff), ct);
        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.ChecklistTemplates.IgnoreQueryFilters().Where(template => template.IsDeleted && template.DeletedAtUtc < cutoff), ct);

#pragma warning disable ORBIT0004 // suggestionCutoff removes unimported calendar suggestions by DiscoveredAtUtc age after SuggestionRetentionPeriod. https://github.com/thomasluizon/orbit-tickets/issues/230
        var suggestionCutoff = DateTime.UtcNow - SuggestionRetentionPeriod;
#pragma warning restore ORBIT0004
        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.GoogleCalendarSyncSuggestions
            .Where(suggestion => suggestion.DiscoveredAtUtc < suggestionCutoff && suggestion.ImportedAtUtc == null), ct);
        var registrationCutoffAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime - OAuthRegistrationRetentionPeriod;
        totalPurged += await BatchedDelete.ExecuteAsync(dbContext.OAuthClients
            .Where(client => client.CreatedAtUtc < registrationCutoffAtUtc && client.AuthorizedAtUtc == null), ct);
        return totalPurged;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "SyncCleanupService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "SyncCleanupService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in sync cleanup")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Purged {Count} records outside their retention windows")]
    private static partial void LogEntitiesPurged(ILogger logger, int count);
}
