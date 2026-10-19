using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Auth.Jobs;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public sealed class OwedSubscriptionCancellationService(
    IServiceScopeFactory scopeFactory,
    ILogger<OwedSubscriptionCancellationService> logger,
    TimeProvider clock) : ScheduledServiceBase, IScheduledJob
{
    public string Name => "owed-subscription-cancellation";
    public string CronExpression => "* * * * *";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        var ids = await context.OwedSubscriptionCancellations.AsNoTracking()
            .Where(record => record.NextAttemptAtUtc <= nowAtUtc)
            .Select(record => record.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            using var attemptScope = scopeFactory.CreateScope();
            try
            {
                await attemptScope.ServiceProvider.GetRequiredService<OwedSubscriptionCancellationJob>()
                    .ExecuteAsync(id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to process owed cancellation {CancellationId}", id);
            }
        }
    }

    protected override Task ExecuteTickAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);
    protected override void LogStarted() => logger.LogInformation("Owed cancellation service started");
    protected override void LogStopped() => logger.LogInformation("Owed cancellation service stopped");
    protected override void LogTickError(Exception ex) => logger.LogError(ex, "Owed cancellation scan failed");
}
