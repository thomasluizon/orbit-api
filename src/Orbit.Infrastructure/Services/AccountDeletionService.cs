using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class AccountDeletionService(
    IServiceScopeFactory scopeFactory,
    ILogger<AccountDeletionService> logger,
    IConfiguration configuration) : BackgroundService, IScheduledJob
{
    private readonly TimeSpan _interval = TimeSpan.FromHours(
        configuration.GetValue("BackgroundServices:AccountDeletionIntervalHours", 24));

    public string Name => "account-deletion";

    public string CronExpression => "0 3 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ProcessScheduledDeletions(cancellationToken);
        await CleanupStaleSentRecords(cancellationToken);
        BackgroundServiceHealthCheck.RecordTick("AccountDeletion");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

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
                    LogServiceError(logger, ex);
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        finally
        {
            LogServiceStopped(logger);
        }
    }

    private async Task ProcessScheduledDeletions(CancellationToken ct)
    {
        var userIds = await GetUsersScheduledForDeletionAsync(ct);

        if (userIds.Count == 0)
            return;

        if (logger.IsEnabled(LogLevel.Information))
            LogProcessingDeletions(logger, userIds.Count);

        foreach (var userId in userIds)
            await DeleteUserAccountAsync(userId, ct);
    }

    private async Task<IReadOnlyList<Guid>> GetUsersScheduledForDeletionAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        return await dbContext.Users
            .Where(u => u.IsDeactivated && u.ScheduledDeletionAt.HasValue && u.ScheduledDeletionAt.Value <= DateTime.UtcNow)
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    private async Task DeleteUserAccountAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

            var userToDelete = await dbContext.Users.FindAsync([userId], ct);
            if (userToDelete is not null)
            {
                var resetRepository = scope.ServiceProvider.GetRequiredService<IAccountResetRepository>();
                await resetRepository.DeleteAllUserDataAsync(userId, ct);
                dbContext.Users.Remove(userToDelete);
                await dbContext.SaveChangesAsync(ct);
            }

            if (logger.IsEnabled(LogLevel.Information))
                LogAccountDeleted(logger, userId);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                LogAccountDeletionFailed(logger, ex, userId);
        }
    }

    internal async Task CleanupStaleSentRecords(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));

        var deletedReminders = await dbContext.SentReminders
            .Where(r => r.Date < cutoff)
            .ExecuteDeleteAsync(ct);

        var deletedSlipAlerts = await dbContext.SentSlipAlerts
            .Where(a => a.WeekStart < cutoff)
            .ExecuteDeleteAsync(ct);

        var deletedStreakFreezeAlerts = await dbContext.SentStreakFreezeAlerts
            .Where(a => a.FrozenDate < cutoff)
            .ExecuteDeleteAsync(ct);

        if ((deletedReminders > 0 || deletedSlipAlerts > 0 || deletedStreakFreezeAlerts > 0) && logger.IsEnabled(LogLevel.Information))
            LogStaleRecordsCleaned(logger, deletedReminders, deletedSlipAlerts, deletedStreakFreezeAlerts);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "AccountDeletionService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "AccountDeletionService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in account deletion service")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Processing {Count} scheduled account deletions")]
    private static partial void LogProcessingDeletions(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Deleted deactivated account {UserId}")]
    private static partial void LogAccountDeleted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to delete account {UserId}")]
    private static partial void LogAccountDeletionFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Cleaned up {Reminders} stale SentReminders, {SlipAlerts} stale SentSlipAlerts, and {StreakFreezeAlerts} stale SentStreakFreezeAlerts older than 90 days")]
    private static partial void LogStaleRecordsCleaned(ILogger logger, int reminders, int slipAlerts, int streakFreezeAlerts);

}
