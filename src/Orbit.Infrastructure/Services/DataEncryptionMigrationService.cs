using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// One-time startup service that encrypts all existing plaintext data.
/// Uses an AppConfig flag ("EncryptionMigrationComplete") to only run once.
/// Processes entities in small batches to avoid overwhelming the database.
/// Safe to run multiple times (idempotent).
/// </summary>
public sealed partial class DataEncryptionMigrationService(
    IServiceScopeFactory scopeFactory,
    ILogger<DataEncryptionMigrationService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private const string MigrationFlag = "EncryptionMigrationComplete";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        try
        {
            if (await HasAlreadyRun(stoppingToken))
            {
                LogMigrationAlreadyComplete(logger);
                return;
            }

            LogStartingMigration(logger);

            var success = true;
            success &= await MigrateEntities<Habit>("Habits", stoppingToken);
            success &= await MigrateEntities<HabitLog>("HabitLogs", stoppingToken);
            success &= await MigrateUserFacts(stoppingToken);
            success &= await MigrateEntities<Goal>("Goals", stoppingToken);
            success &= await MigrateEntities<GoalProgressLog>("GoalProgressLogs", stoppingToken);

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
    private async Task<bool> MigrateEntities<T>(string entityName, CancellationToken stoppingToken) where T : class
    {
        var totalProcessed = 0;
        var hadErrors = false;
        var offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

                var batch = await db.Set<T>()
                    .OrderBy(e => EF.Property<Guid>(e, "Id"))
                    .Skip(offset)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                    break;

                foreach (var entity in batch)
                    db.Entry(entity).State = EntityState.Modified;

                await db.SaveChangesAsync(stoppingToken);
                totalProcessed += batch.Count;
                offset += batch.Count;
            }
            catch (Exception ex)
            {
                LogBatchFailed(logger, ex, entityName, offset);
                hadErrors = true;
                offset += BatchSize;
            }
        }

        if (totalProcessed > 0)
            LogEntitiesEncrypted(logger, totalProcessed, entityName);

        return !hadErrors;
    }

    /// <summary>
    /// UserFact has a global query filter (IsDeleted), so we need IgnoreQueryFilters
    /// to encrypt soft-deleted facts too.
    /// </summary>
    private async Task<bool> MigrateUserFacts(CancellationToken stoppingToken)
    {
        var totalProcessed = 0;
        var hadErrors = false;
        var offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

                var batch = await db.UserFacts
                    .IgnoreQueryFilters()
                    .OrderBy(f => f.Id)
                    .Skip(offset)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                    break;

                foreach (var fact in batch)
                    db.Entry(fact).State = EntityState.Modified;

                await db.SaveChangesAsync(stoppingToken);
                totalProcessed += batch.Count;
                offset += batch.Count;
            }
            catch (Exception ex)
            {
                LogUserFactsBatchFailed(logger, ex, offset);
                hadErrors = true;
                offset += BatchSize;
            }
        }

        if (totalProcessed > 0)
            LogUserFactsEncrypted(logger, totalProcessed);

        return !hadErrors;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Encryption migration already completed -- skipping")]
    private static partial void LogMigrationAlreadyComplete(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Starting full data encryption migration")]
    private static partial void LogStartingMigration(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Full data encryption migration completed successfully")]
    private static partial void LogMigrationCompleted(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Encryption migration had errors -- will retry on next startup")]
    private static partial void LogMigrationHadErrors(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Data encryption migration failed -- will retry on next startup")]
    private static partial void LogMigrationFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Batch failed for {Entity} at offset {Offset} -- skipping batch")]
    private static partial void LogBatchFailed(ILogger logger, Exception ex, string entity, int offset);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Encrypted {Count} {Entity}")]
    private static partial void LogEntitiesEncrypted(ILogger logger, int count, string entity);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Batch failed for UserFacts at offset {Offset} -- skipping batch")]
    private static partial void LogUserFactsBatchFailed(ILogger logger, Exception ex, int offset);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Encrypted {Count} UserFacts")]
    private static partial void LogUserFactsEncrypted(ILogger logger, int count);

}
