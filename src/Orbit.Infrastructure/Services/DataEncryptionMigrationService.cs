using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// One-time startup service that encrypts existing plaintext data and computes EmailHash
/// for users that don't have one yet. Runs once on application startup, then completes.
/// Safe to run multiple times (idempotent) -- skips already-encrypted data.
/// </summary>
public sealed class DataEncryptionMigrationService(
    IServiceScopeFactory scopeFactory,
    ILogger<DataEncryptionMigrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let the app finish starting
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        try
        {
            await MigrateEmailHashes(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Data encryption migration failed");
        }
    }

    private async Task MigrateEmailHashes(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

        // Find users with empty EmailHash (pre-encryption data)
        // Note: We read raw from the database to avoid the ValueConverter decrypting
        // Use a raw SQL query to check for NULL or empty EmailHash
        var usersToMigrate = await dbContext.Users
            .Where(u => u.EmailHash == null! || u.EmailHash == "")
            .ToListAsync(stoppingToken);

        if (usersToMigrate.Count == 0)
        {
            logger.LogInformation("No users need EmailHash migration");
            return;
        }

        logger.LogInformation("Migrating EmailHash for {Count} users", usersToMigrate.Count);

        foreach (var user in usersToMigrate)
        {
            // The Email is already decrypted by the ValueConverter at this point
            var emailHash = encryptionService.ComputeHmac(user.Email);
            user.SetEmailHash(emailHash);
        }

        await dbContext.SaveChangesAsync(stoppingToken);
        logger.LogInformation("EmailHash migration complete for {Count} users", usersToMigrate.Count);
    }
}
