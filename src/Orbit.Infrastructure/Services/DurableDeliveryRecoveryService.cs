using Hangfire;
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
        var pending = await db.MarketingDeliveries.AsNoTracking()
            .Where(row => row.SentAtUtc == null && row.NotBeforeAtUtc <= nowAtUtc)
            .OrderBy(row => row.NotBeforeAtUtc).ToListAsync(cancellationToken);
        foreach (var row in pending)
            jobs.Enqueue<SendMarketingEmailJob>(job => job.ExecuteRecipientAsync(row.BroadcastId, row.RecipientId, row.Email, row.Language));

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
