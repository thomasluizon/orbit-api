using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public sealed class DurableDeliveryRecoveryService(IServiceScopeFactory scopes, ILogger<DurableDeliveryRecoveryService> logger)
    : ScheduledServiceBase, IScheduledJob
{
    public string Name => "durable-delivery-recovery";
    public string CronExpression => "* * * * *";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
        var nowAtUtc = DateTime.UtcNow;
        var dispatchUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await dispatchUnitOfWork.ExecuteOnceInTransactionAsync(async token =>
        {
            await dispatchUnitOfWork.AcquireAdvisoryLockAsync("marketing-dispatch", token);
            var pending = await db.MarketingDeliveries
                .Where(row => row.SentAtUtc == null && row.NotBeforeAtUtc <= nowAtUtc && row.NextRecoveryAtUtc <= nowAtUtc)
                .OrderBy(row => row.NextRecoveryAtUtc).ThenBy(row => row.Id).Take(100).ToListAsync(token);
            if (pending.Count == 0)
                return;
            using var connection = scope.ServiceProvider.GetRequiredService<JobStorage>().GetConnection();
            foreach (var row in pending)
            {
                var state = row.BackgroundJobId is null ? null : connection.GetStateData(row.BackgroundJobId);
                if (state is not null && state.Name != FailedState.StateName && state.Name != DeletedState.StateName &&
                    state.Name != SucceededState.StateName)
                {
                    row.DeferRecovery(nowAtUtc);
                    continue;
                }
                var jobId = jobs.Enqueue<SendMarketingEmailJob>(job => job.ExecuteRecipientAsync(
                    row.BroadcastId, row.RecipientId, row.Email, row.Language));
                if (jobId is not null)
                    row.RecordDispatch(jobId, nowAtUtc);
                else
                    row.DeferRecovery(nowAtUtc);
            }
            await db.SaveChangesAsync(token);
        }, cancellationToken);

        var coupons = await db.PendingCouponCancellations.AsNoTracking()
            .Where(row => row.NextAttemptAtUtc <= nowAtUtc).Select(row => row.Id).Take(100).ToListAsync(cancellationToken);
        foreach (var id in coupons)
        {
            using var attemptScope = scopes.CreateScope();
            var attemptDb = attemptScope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            var unitOfWork = attemptScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ExecuteOnceInTransactionAsync(async token =>
            {
                await unitOfWork.AcquireAdvisoryLockAsync($"coupon-cancel:{id}", token);
                var row = await attemptDb.PendingCouponCancellations.SingleOrDefaultAsync(row => row.Id == id, token);
                if (row is null || row.NextAttemptAtUtc > nowAtUtc)
                    return;
                try
                {
                    await attemptScope.ServiceProvider.GetRequiredService<IReferralRewardService>().CancelCouponAsync(row.CouponId, token);
                    attemptDb.PendingCouponCancellations.Remove(row);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    row.RecordFailure(nowAtUtc);
                    logger.LogWarning(ex, "Coupon cancellation will retry for user {UserId}", row.UserId);
                }
                await attemptDb.SaveChangesAsync(token);
            }, cancellationToken);
        }
    }

    protected override Task ExecuteTickAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);
    protected override void LogStarted() => logger.LogInformation("Delivery recovery started");
    protected override void LogStopped() => logger.LogInformation("Delivery recovery stopped");
    protected override void LogTickError(Exception ex) => logger.LogError(ex, "Delivery recovery failed");
}
