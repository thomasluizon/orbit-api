using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.BackgroundJobs;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// One-time startup service that backfills historical <see cref="XpAwardLog"/> rows for every existing
/// user through <see cref="XpAwardLogBackfillService"/>. Guards on an AppConfig flag so the sweep runs
/// once; the per-user idempotency check inside the backfill keeps a re-run safe regardless.
/// </summary>
public sealed partial class XpAwardLogBackfillHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<XpAwardLogBackfillHostedService> logger,
    IScheduledJobLeaseProvider leases) : BackgroundService
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
            await using var lease = await leases.TryAcquireAsync("xp-award-backfill", stoppingToken);
            if (lease is null) return;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

            if (await db.AppConfigs.AnyAsync(c => c.Key == BackfillFlag, stoppingToken))
            {
                LogBackfillAlreadyComplete(logger);
                return;
            }

            var service = scope.ServiceProvider.GetRequiredService<XpAwardLogBackfillService>();
            var checkpoint = await db.AppConfigs.SingleOrDefaultAsync(row => row.Key == "XpAwardLogBackfillCursor", stoppingToken);
            Guid? cursor = checkpoint is not null && Guid.TryParse(checkpoint.Value, out var saved) ? saved : null;
            var processed = 0;
            while (true)
            {
                var ids = await db.Users.AsNoTracking().Page(cursor, 200).Select(user => user.Id).ToListAsync(stoppingToken);
                if (ids.Count == 0) break;
                foreach (var id in ids)
                {
                    if (await service.BackfillUserAsync(id, stoppingToken)) processed++;
                    db.ChangeTracker.Clear();
                }
                cursor = ids[^1];
                checkpoint = await db.AppConfigs.SingleOrDefaultAsync(row => row.Key == "XpAwardLogBackfillCursor", stoppingToken);
                if (checkpoint is null)
                    db.AppConfigs.Add(AppConfig.Create("XpAwardLogBackfillCursor", cursor.Value.ToString(), "Last completed XP backfill page"));
                else checkpoint.SetValue(cursor.Value.ToString());
                await db.SaveChangesAsync(stoppingToken);
                db.ChangeTracker.Clear();
            }

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
