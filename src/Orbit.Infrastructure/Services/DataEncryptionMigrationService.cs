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
                logger.LogInformation("Encryption migration already completed -- skipping");
                return;
            }

            logger.LogInformation("Starting full data encryption migration");

            var success = true;
            success &= await MigrateEntities<Habit>("Habits", stoppingToken);
            success &= await MigrateEntities<HabitLog>("HabitLogs", stoppingToken);
            success &= await MigrateUserFacts(stoppingToken);
            success &= await MigrateEntities<Goal>("Goals", stoppingToken);
            success &= await MigrateEntities<GoalProgressLog>("GoalProgressLogs", stoppingToken);

            if (success)
            {
                await SetMigrationComplete(stoppingToken);
                logger.LogInformation("Full data encryption migration completed successfully");
            }
            else
            {
                logger.LogWarning("Encryption migration had errors -- will retry on next startup");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Data encryption migration failed -- will retry on next startup");
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
                logger.LogWarning(ex, "Batch failed for {Entity} at offset {Offset} -- skipping batch", entityName, offset);
                hadErrors = true;
                offset += BatchSize;
            }
        }

        if (totalProcessed > 0)
            logger.LogInformation("Encrypted {Count} {Entity}", totalProcessed, entityName);

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
                logger.LogWarning(ex, "Batch failed for UserFacts at offset {Offset} -- skipping batch", offset);
                hadErrors = true;
                offset += BatchSize;
            }
        }

        if (totalProcessed > 0)
            logger.LogInformation("Encrypted {Count} UserFacts", totalProcessed);

        return !hadErrors;
    }
}
