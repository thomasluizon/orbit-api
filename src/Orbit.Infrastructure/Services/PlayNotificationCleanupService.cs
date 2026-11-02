using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Daily background service that purges processed billing dedup records (Play RTDN messages and
/// Stripe webhook events) older than their provider redelivery windows, keeping the idempotency
/// tables bounded.
/// </summary>
public partial class PlayNotificationCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<PlayNotificationCleanupService> logger, TimeProvider? clock = null) : BackgroundService, IScheduledJob
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan PlayRetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan StripeRetentionPeriod = TimeSpan.FromDays(90);

    public string Name => "play-notification-cleanup";

    public string CronExpression => "0 4 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PurgeOldNotifications(cancellationToken);
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
                    await PurgeOldNotifications(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogServiceError(logger, ex);
                }

                await Task.Delay(Interval, clock ?? TimeProvider.System, stoppingToken);
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

        var nowAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var failures = new List<Exception>();
        await PurgeAsync(async () =>
        {
            var count = await BatchedDelete.ExecuteAsync(dbContext.ProcessedPlayNotifications
                .Where(n => n.ProcessedAtUtc < nowAtUtc - PlayRetentionPeriod
                    && n.ConsumedCouponId == null && n.ProcessingLeaseId == null), ct);
            if (count > 0) LogNotificationsPurged(logger, count);
        }, failures, "ProcessedPlayNotifications");
        await PurgeAsync(async () =>
        {
            var count = await BatchedDelete.ExecuteAsync(dbContext.ProcessedStripeEvents
                .Where(e => e.ProcessedAtUtc < nowAtUtc - StripeRetentionPeriod), ct);
            if (count > 0) LogStripeEventsPurged(logger, count);
        }, failures, "ProcessedStripeEvents");
        if (failures.Count > 0) throw new AggregateException("Billing history cleanup failed.", failures);
    }

    private async Task PurgeAsync(Func<Task> purge, List<Exception> failures, string table)
    {
        try { await purge(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add(exception);
            LogPurgeFailed(logger, exception, table);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PlayNotificationCleanupService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PlayNotificationCleanupService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Billing history cleanup failed")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Purged {Count} processed Play notifications older than 30 days")]
    private static partial void LogNotificationsPurged(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Purged {Count} processed Stripe events older than 90 days")]
    private static partial void LogStripeEventsPurged(ILogger logger, int count);
    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Billing history purge failed for {Table}")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception, string table);

}
