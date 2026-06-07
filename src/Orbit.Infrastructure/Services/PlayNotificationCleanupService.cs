using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Daily background service that purges processed Play RTDN dedup records older than the
/// Pub/Sub redelivery window, keeping the idempotency table bounded.
/// </summary>
public partial class PlayNotificationCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<PlayNotificationCleanupService> logger) : BackgroundService
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
                    await PurgeOldNotifications(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("PlayNotificationCleanup");
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

    private async Task PurgeOldNotifications(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var cutoff = DateTime.UtcNow - RetentionPeriod;
        var purged = await dbContext.ProcessedPlayNotifications
            .Where(n => n.ProcessedAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);

        if (purged > 0)
            LogNotificationsPurged(logger, purged);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PlayNotificationCleanupService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PlayNotificationCleanupService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error purging processed Play notifications")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Purged {Count} processed Play notifications older than 30 days")]
    private static partial void LogNotificationsPurged(ILogger logger, int count);
}
