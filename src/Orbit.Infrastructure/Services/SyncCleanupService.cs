using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Daily background service that purges soft-deleted entities older than 30 days.
/// </summary>
public partial class SyncCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

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

    private async Task PurgeSoftDeletedEntities(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var cutoff = DateTime.UtcNow - RetentionPeriod;
        var totalPurged = 0;

        // Purge soft-deleted habits (using IgnoreQueryFilters to access deleted records)
        var deletedHabits = await dbContext.Habits
            .IgnoreQueryFilters()
            .Where(h => h.IsDeleted && h.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedHabits.Count > 0)
        {
            dbContext.Habits.RemoveRange(deletedHabits);
            totalPurged += deletedHabits.Count;
        }

        // Purge soft-deleted goals
        var deletedGoals = await dbContext.Goals
            .IgnoreQueryFilters()
            .Where(g => g.IsDeleted && g.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedGoals.Count > 0)
        {
            dbContext.Goals.RemoveRange(deletedGoals);
            totalPurged += deletedGoals.Count;
        }

        // Purge soft-deleted tags
        var deletedTags = await dbContext.Tags
            .IgnoreQueryFilters()
            .Where(t => t.IsDeleted && t.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedTags.Count > 0)
        {
            dbContext.Tags.RemoveRange(deletedTags);
            totalPurged += deletedTags.Count;
        }

        // Purge soft-deleted user facts
        var deletedFacts = await dbContext.UserFacts
            .IgnoreQueryFilters()
            .Where(f => f.IsDeleted && f.DeletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (deletedFacts.Count > 0)
        {
            dbContext.UserFacts.RemoveRange(deletedFacts);
            totalPurged += deletedFacts.Count;
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

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Purged {Count} soft-deleted entities older than 30 days")]
    private static partial void LogEntitiesPurged(ILogger logger, int count);
}
