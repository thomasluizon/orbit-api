using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public sealed class AuthStateCleanupService(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<AuthStateCleanupService> logger)
    : ScheduledServiceBase, IScheduledJob
{
    public string Name => "auth-state-cleanup";
    public string CronExpression => "*/15 * * * *";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        var policies = await db.DistributedRateLimitBuckets.AsNoTracking().Select(row => row.PolicyName)
            .Distinct().ToListAsync(cancellationToken);
        foreach (var policy in policies)
            await db.DistributedRateLimitBuckets.Where(row => row.PolicyName == policy && row.WindowEndsAtUtc <= nowAtUtc)
                .ExecuteDeleteAsync(cancellationToken);
        var cooldownCutoffAtUtc = nowAtUtc.AddSeconds(-60);
        await db.EmailChallenges.Where(row => row.ExpiresAtUtc < nowAtUtc
            && row.AttemptWindowEndsAtUtc < nowAtUtc && row.IssuedAtUtc < cooldownCutoffAtUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    protected override Task ExecuteTickAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);
    protected override void LogStarted() => logger.LogInformation("Auth state cleanup started");
    protected override void LogStopped() => logger.LogInformation("Auth state cleanup stopped");
    protected override void LogTickError(Exception ex) => logger.LogError(ex, "Auth state cleanup failed");
}
