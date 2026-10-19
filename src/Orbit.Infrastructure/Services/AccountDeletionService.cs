using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Marketing.Services;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
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

        if (logger.IsEnabled(LogLevel.Debug))
            LogProcessingDeletions(logger, userIds.Count);

        foreach (var userId in userIds)
            await DeleteUserAccountAsync(userId, ct);
    }

    private async Task<IReadOnlyList<Guid>> GetUsersScheduledForDeletionAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        return await dbContext.Users
            .IgnoreQueryFilters()
#pragma warning disable ORBIT0004
            .Where(u => u.IsDeactivated && u.ScheduledDeletionAt.HasValue && u.ScheduledDeletionAt.Value <= DateTime.UtcNow)
#pragma warning restore ORBIT0004
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    private async Task DeleteUserAccountAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            var user = await dbContext.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            var nowAtUtc = DateTime.UtcNow;
            if (user is not { IsDeactivated: true, ScheduledDeletionAt: not null } || user.ScheduledDeletionAt > nowAtUtc)
                return;

            await StopRenewalsBeforeErasureAsync(scope.ServiceProvider, user, nowAtUtc, ct);
            unitOfWork.ResetTracking();
            var deleted = await unitOfWork.ExecuteInTransactionAsync(
                transactionToken => DeleteLockedAccountAsync(scope.ServiceProvider, userId,
                    user.StripeSubscriptionId, user.PlayPurchaseToken, transactionToken), ct);

            if (deleted && logger.IsEnabled(LogLevel.Information))
                LogAccountDeleted(logger, userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Error))
                LogAccountDeletionFailed(logger, ex, userId);
        }
    }

    private async Task<bool> DeleteLockedAccountAsync(IServiceProvider services, Guid userId,
        string? stripeSubscriptionId, string? playPurchaseToken, CancellationToken ct)
    {
        var dbContext = services.GetRequiredService<OrbitDbContext>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        await unitOfWork.AcquireUserRowLockAsync(userId, ct);
        var user = await dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        var nowAtUtc = DateTime.UtcNow;
        if (user is not { IsDeactivated: true, ScheduledDeletionAt: not null } || user.ScheduledDeletionAt > nowAtUtc)
        {
            LogErasureRecheckSkipped(logger, userId, "account is no longer due for deletion");
            return false;
        }
        if (user.StripeSubscriptionId != stripeSubscriptionId || user.PlayPurchaseToken != playPurchaseToken)
        {
            LogErasureRecheckSkipped(logger, userId, "subscription identifiers changed");
            return false;
        }

        var contacts = services.GetRequiredService<IGenericRepository<MarketingContact>>();
        await MarketingContactOptOut.RecordAsync(user.Email, contacts, unitOfWork, ct);
        var stores = services.GetServices<IObjectStorageDeletionService>().ToArray();
        if (stores.Length == 0)
            throw new InvalidOperationException("No upload deletion providers are configured.");
        foreach (var store in stores)
            await DeleteObjectsWithRetryAsync(store, userId, ct);

        await dbContext.AiUsageDaily.Where(u => u.UserId == userId).ExecuteDeleteAsync(ct);
        await services.GetRequiredService<IAccountResetRepository>().DeleteAllUserDataAsync(userId, ct);
        dbContext.Users.Remove(user);
        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }

    private async Task StopRenewalsBeforeErasureAsync(IServiceProvider services, User user, DateTime nowAtUtc,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.StripeSubscriptionId) && string.IsNullOrWhiteSpace(user.PlayPurchaseToken))
            return;

        if (nowAtUtc - user.ScheduledDeletionAt!.Value < MaxBillingDeferral)
        {
            await services.GetRequiredService<CancelSubscriptionOnDeletionJob>().CancelBeforeDeletionAsync(user, ct);
            return;
        }

        var jobs = services.GetRequiredService<IBackgroundJobClient>();
        var jobId = jobs.Enqueue<CancelSubscriptionOnDeletionJob>(job => job.ExecuteAsync(user.Id,
            user.StripeSubscriptionId, user.PlayPurchaseToken, true, CancellationToken.None));
        if (jobId is null)
            throw new InvalidOperationException("Billing cancellation job was not created.");
        LogCancellationDeferred(logger, user.Id, jobId);
    }

    private async Task DeleteObjectsWithRetryAsync(
        IObjectStorageDeletionService store, Guid userId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await store.DeleteUserObjectsAsync(userId, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 2)
            {
                LogStorageDeletionRetry(logger, ex, userId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(300 << attempt), ct);
            }
        }
    }

    internal async Task CleanupStaleSentRecords(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

#pragma warning disable ORBIT0004
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
#pragma warning restore ORBIT0004

        var deletedReminders = await dbContext.SentReminders
            .Where(r => r.Date < cutoff)
            .ExecuteDeleteAsync(ct);

        var deletedSlipAlerts = await dbContext.SentSlipAlerts
            .Where(a => a.WeekStart < cutoff)
            .ExecuteDeleteAsync(ct);

#pragma warning disable ORBIT0004
        var processedRequestCutoff = DateTime.UtcNow.AddDays(-ProcessedRequestRetentionDays);
#pragma warning restore ORBIT0004
        var deletedProcessedRequests = await dbContext.ProcessedRequests
            .Where(r => r.CreatedAtUtc < processedRequestCutoff)
            .ExecuteDeleteAsync(ct);

        if ((deletedReminders > 0 || deletedSlipAlerts > 0 || deletedProcessedRequests > 0) && logger.IsEnabled(LogLevel.Information))
            LogStaleRecordsCleaned(logger, deletedReminders, deletedSlipAlerts, deletedProcessedRequests);
    }

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Retrying upload deletion for user {UserId} after attempt {Attempt}")]
    private static partial void LogStorageDeletionRetry(ILogger logger, Exception ex, Guid userId, int attempt);

    private const int ProcessedRequestRetentionDays = 30;
    private static readonly TimeSpan MaxBillingDeferral = TimeSpan.FromDays(7);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "AccountDeletionService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "AccountDeletionService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in account deletion service")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Processing {Count} scheduled account deletions")]
    private static partial void LogProcessingDeletions(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Deleted deactivated account {UserId}")]
    private static partial void LogAccountDeleted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to delete account {UserId}")]
    private static partial void LogAccountDeletionFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Cleaned up {Reminders} stale SentReminders and {SlipAlerts} stale SentSlipAlerts older than 90 days, plus {ProcessedRequests} ProcessedRequests older than 30 days")]
    private static partial void LogStaleRecordsCleaned(ILogger logger, int reminders, int slipAlerts, int processedRequests);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Billing cancellation for account {UserId} is unconfirmed past the erasure deferral limit; cancellation job {JobId} queued before erasure")]
    private static partial void LogCancellationDeferred(ILogger logger, Guid userId, string jobId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Skipped account erasure for user {UserId}: {Reason}")]
    private static partial void LogErasureRecheckSkipped(ILogger logger, Guid userId, string reason);

}
