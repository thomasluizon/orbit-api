using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// One-time startup service that encrypts all existing plaintext data and computes EmailHash.
/// Uses an AppConfig flag ("EncryptionMigrationComplete") to only run once.
/// Processes entities in small batches to avoid overwhelming the database.
/// Safe to run multiple times (idempotent).
/// </summary>
public sealed class DataEncryptionMigrationService(
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

            await MigrateEmailHashes(stoppingToken);
            await MigrateEntities<User>("Users", stoppingToken);
            await MigrateEntities<Habit>("Habits", stoppingToken);
            await MigrateEntities<HabitLog>("HabitLogs", stoppingToken);
            await MigrateUserFacts(stoppingToken);
            await MigrateEntities<PushSubscription>("PushSubscriptions", stoppingToken);
            await MigrateEntities<Goal>("Goals", stoppingToken);
            await MigrateEntities<GoalProgressLog>("GoalProgressLogs", stoppingToken);

            await SetMigrationComplete(stoppingToken);
            logger.LogInformation("Full data encryption migration completed successfully");
        }
        catch (Exception ex)
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

    private async Task MigrateEmailHashes(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

        var usersToMigrate = await db.Users
            .Where(u => u.EmailHash == null! || u.EmailHash == "")
            .ToListAsync(stoppingToken);

        if (usersToMigrate.Count == 0)
            return;

        logger.LogInformation("Migrating EmailHash for {Count} users", usersToMigrate.Count);

        foreach (var user in usersToMigrate)
        {
            var emailHash = encryptionService.ComputeHmac(user.Email);
            user.SetEmailHash(emailHash);
        }

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("EmailHash migration complete");
    }

    /// <summary>
    /// Loads all entities of a type in batches, marks them as Modified, and saves.
    /// The ValueConverter encrypts on write -- so loading (decrypt/passthrough) then saving
    /// (encrypt) converts all plaintext to ciphertext.
    /// Already-encrypted data is decrypted then re-encrypted (harmless, new nonce).
    /// </summary>
    private async Task MigrateEntities<T>(string entityName, CancellationToken stoppingToken) where T : class
    {
        var totalProcessed = 0;
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
                offset += BatchSize;
            }
        }

        if (totalProcessed > 0)
            logger.LogInformation("Encrypted {Count} {Entity}", totalProcessed, entityName);
    }

    /// <summary>
    /// UserFact has a global query filter (IsDeleted), so we need IgnoreQueryFilters
    /// to encrypt soft-deleted facts too.
    /// </summary>
    private async Task MigrateUserFacts(CancellationToken stoppingToken)
    {
        var totalProcessed = 0;
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
                offset += BatchSize;
            }
        }

        if (totalProcessed > 0)
            logger.LogInformation("Encrypted {Count} UserFacts", totalProcessed);
    }
}
