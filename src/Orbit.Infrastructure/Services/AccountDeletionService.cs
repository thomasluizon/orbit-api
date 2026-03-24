using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class AccountDeletionService(
    IServiceScopeFactory scopeFactory,
    ILogger<AccountDeletionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AccountDeletionService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessScheduledDeletions(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in account deletion service");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task ProcessScheduledDeletions(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var usersToDelete = await dbContext.Users
            .Where(u => u.IsDeactivated && u.ScheduledDeletionAt.HasValue && u.ScheduledDeletionAt.Value <= DateTime.UtcNow)
            .ToListAsync(ct);

        if (usersToDelete.Count == 0)
            return;

        logger.LogInformation("Processing {Count} scheduled account deletions", usersToDelete.Count);

        foreach (var user in usersToDelete)
        {
            try
            {
                dbContext.Users.Remove(user);
                await dbContext.SaveChangesAsync(ct);
                logger.LogInformation("Deleted deactivated account {UserId} ({Email})", user.Id, user.Email);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete account {UserId}", user.Id);
            }
        }
    }
}
