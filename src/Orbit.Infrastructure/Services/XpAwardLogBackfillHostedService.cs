using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// One-time startup service that backfills historical <see cref="XpAwardLog"/> rows for every existing
/// user through <see cref="XpAwardLogBackfillService"/>. Guards on an AppConfig flag so the sweep runs
/// once; the per-user idempotency check inside the backfill keeps a re-run safe regardless.
/// </summary>
public sealed partial class XpAwardLogBackfillHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<XpAwardLogBackfillHostedService> logger) : BackgroundService
{
    private const string BackfillFlag = "XpAwardLogBackfillComplete";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        await RunBackfillAsync(stoppingToken);
    }

    internal async Task RunBackfillAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

            if (await db.AppConfigs.AnyAsync(c => c.Key == BackfillFlag, stoppingToken))
            {
                LogBackfillAlreadyComplete(logger);
                return;
            }

            var service = scope.ServiceProvider.GetRequiredService<XpAwardLogBackfillService>();
            var processed = await service.BackfillAllAsync(stoppingToken);

            db.AppConfigs.Add(AppConfig.Create(BackfillFlag, "true", "Set automatically after the one-time XP award-log backfill"));
            await db.SaveChangesAsync(stoppingToken);

            LogBackfillCompleted(logger, processed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBackfillFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "XP award-log backfill already completed -- skipping")]
    private static partial void LogBackfillAlreadyComplete(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "XP award-log backfill completed for {Count} users")]
    private static partial void LogBackfillCompleted(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "XP award-log backfill failed -- will retry on next startup")]
    private static partial void LogBackfillFailed(ILogger logger, Exception ex);
}
