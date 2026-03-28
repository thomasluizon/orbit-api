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
                await CleanupStaleSentRecords(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in account deletion service");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
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
