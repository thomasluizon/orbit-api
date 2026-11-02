using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.BackgroundJobs;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// One-time startup service that encrypts all existing plaintext data.
/// Uses an AppConfig flag ("EncryptionMigrationComplete") to only run once.
/// Processes entities in small batches to avoid overwhelming the database.
/// Safe to run multiple times (idempotent).
/// </summary>
public sealed partial class DataEncryptionMigrationService(
    IServiceScopeFactory scopeFactory,
    IEncryptionService encryptionService,
    ILogger<DataEncryptionMigrationService> logger,
    IScheduledJobLeaseProvider leases) : BackgroundService
{
    private const int BatchSize = 50;

    private const string MigrationFlag = "EncryptionMigrationComplete_v2";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        await RunMigrationAsync(stoppingToken);
    }

    internal async Task RunMigrationAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var lease = await leases.TryAcquireAsync("encryption-backfill", stoppingToken);
            if (lease is null) return;
            if (await HasAlreadyRun(stoppingToken))
            {
                LogMigrationAlreadyComplete(logger);
                return;
            }

            if (!encryptionService.IsConfigured)
            {
                LogMigrationSkippedNotConfigured(logger);
                return;
            }

            LogStartingMigration(logger);

            var success = true;
            success &= await MigrateEntities<Habit>("Habits", stoppingToken);
            success &= await MigrateEntities<HabitLog>("HabitLogs", stoppingToken);
            success &= await MigrateEntities<Goal>("Goals", stoppingToken);
            success &= await MigrateEntities<GoalProgressLog>("GoalProgressLogs", stoppingToken);
            success &= await MigrateEntities<User>("Users", stoppingToken);

            if (success)
            {
                await SetMigrationComplete(stoppingToken);
                LogMigrationCompleted(logger);
            }
            else
            {
                LogMigrationHadErrors(logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMigrationFailed(logger, ex);
        }
    }

    private async Task<bool> HasAlreadyRun(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        return await db.AppConfigs.AnyAsync(c => c.Key == MigrationFlag, stoppingToken);
    }

    private async Task SetMigrationComplete(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        db.AppConfigs.Add(AppConfig.Create(MigrationFlag, "true", "Set automatically after first encryption migration"));
        await db.SaveChangesAsync(stoppingToken);
    }

    /// <summary>
    /// Loads all entities of a type in batches, marks them as Modified, and saves.
    /// The ValueConverter encrypts on write -- so loading (decrypt/passthrough) then saving
    /// (encrypt) converts all plaintext to ciphertext.
    /// Returns false if any batch failed.
    /// </summary>
    private async Task<bool> MigrateEntities<T>(string entityName, CancellationToken stoppingToken) where T : Orbit.Domain.Common.Entity
    {
        var totalProcessed = 0;
        var hadErrors = false;
        Guid? cursor = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

                var batch = await db.Set<T>()
                    .IgnoreQueryFilters()
                    .Page(cursor, BatchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                    break;

                foreach (var entity in batch)
                    db.Entry(entity).State = EntityState.Modified;

                await db.SaveChangesAsync(stoppingToken);
                totalProcessed += batch.Count;
                cursor = batch[^1].Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogBatchFailed(logger, ex, entityName, cursor);
                hadErrors = true;
                break;
            }
        }

        if (totalProcessed > 0 && logger.IsEnabled(LogLevel.Information))
            LogEntitiesEncrypted(logger, totalProcessed, entityName);

        return !hadErrors;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Encryption migration already completed -- skipping")]
    private static partial void LogMigrationAlreadyComplete(ILogger logger);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Encryption key not configured -- skipping migration without marking complete so a configured run can execute later")]
    private static partial void LogMigrationSkippedNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Starting full data encryption migration")]
    private static partial void LogStartingMigration(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Full data encryption migration completed successfully")]
    private static partial void LogMigrationCompleted(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Encryption migration had errors -- will retry on next startup")]
    private static partial void LogMigrationHadErrors(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Data encryption migration failed -- will retry on next startup")]
    private static partial void LogMigrationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Batch failed for {Entity} after {Cursor} -- halting migration, will retry on next startup")]
    private static partial void LogBatchFailed(ILogger logger, Exception ex, string entity, Guid? cursor);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Encrypted {Count} {Entity}")]
    private static partial void LogEntitiesEncrypted(ILogger logger, int count, string entity);

}
