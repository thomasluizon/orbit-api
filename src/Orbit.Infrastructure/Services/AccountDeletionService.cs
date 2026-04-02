using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class AccountDeletionService(
    IServiceScopeFactory scopeFactory,
    ILogger<AccountDeletionService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromHours(
        configuration.GetValue("BackgroundServices:AccountDeletionIntervalHours", 24));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AccountDeletionService started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessScheduledDeletions(stoppingToken);
                    await CleanupStaleSentRecords(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("AccountDeletion");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error in account deletion service");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        finally
        {
            logger.LogInformation("AccountDeletionService stopped");
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
                using var deleteScope = scopeFactory.CreateScope();
                var deleteContext = deleteScope.ServiceProvider.GetRequiredService<OrbitDbContext>();
                var userToDelete = await deleteContext.Users.FindAsync([user.Id], ct);
                if (userToDelete is not null)
                {
                    deleteContext.Users.Remove(userToDelete);
                    await deleteContext.SaveChangesAsync(ct);
                }
                logger.LogInformation("Deleted deactivated account {UserId}", user.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete account {UserId}", user.Id);
            }
        }
    }

    private async Task CleanupStaleSentRecords(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));

        var deletedReminders = await dbContext.SentReminders
            .Where(r => r.Date < cutoff)
            .ExecuteDeleteAsync(ct);

        var cutoffWeek = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var deletedSlipAlerts = await dbContext.SentSlipAlerts
            .Where(a => a.WeekStart < cutoffWeek)
            .ExecuteDeleteAsync(ct);

        if (deletedReminders > 0 || deletedSlipAlerts > 0)
            logger.LogInformation(
                "Cleaned up {Reminders} stale SentReminders and {SlipAlerts} stale SentSlipAlerts older than 90 days",
                deletedReminders, deletedSlipAlerts);
    }
}
