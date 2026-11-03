using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class BackgroundServiceHealthCheck(
    IServiceScopeFactory scopeFactory,
    IEnumerable<IScheduledJob> jobs,
    TimeProvider clock,
    ILogger<BackgroundServiceHealthCheck> logger) : IHealthCheck
{
    internal static readonly IReadOnlyDictionary<string, TimeSpan> ExpectedIntervals = new Dictionary<string, TimeSpan>
    {
        ["reminder-scheduler"] = TimeSpan.FromMinutes(3),
        ["goal-deadline-notification"] = TimeSpan.FromMinutes(90),
        ["slip-alert-scheduler"] = TimeSpan.FromMinutes(15),
        ["proactive-checkin-scheduler"] = TimeSpan.FromHours(3),
        ["habit-due-date-advancement"] = TimeSpan.FromMinutes(90),
        ["streak-goal-sync"] = TimeSpan.FromHours(2),
        ["streak-freeze-auto-activation"] = TimeSpan.FromHours(3),
        ["account-deletion"] = TimeSpan.FromHours(48),
        ["sync-cleanup"] = TimeSpan.FromHours(48),
        ["play-notification-cleanup"] = TimeSpan.FromHours(48),
        ["auth-state-cleanup"] = TimeSpan.FromMinutes(45),
        ["durable-delivery-recovery"] = TimeSpan.FromMinutes(3),
        ["calendar-auto-sync"] = TimeSpan.FromMinutes(45),
        ["notion-auto-sync"] = TimeSpan.FromMinutes(45),
        ["ai-usage-summary"] = TimeSpan.FromHours(48),
        ["owed-subscription-cancellation"] = TimeSpan.FromMinutes(3),
        ["founding-achievement-reconciliation"] = TimeSpan.FromHours(12),
        ["period-close-notification"] = TimeSpan.FromMinutes(90),
        ["chat-turn-retention"] = TimeSpan.FromHours(2),
        ["hangfire-history-retention"] = TimeSpan.FromHours(48)
    };

    internal async Task RegisterAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var registeredAtUtc = clock.GetUtcNow().UtcDateTime;
        foreach (var jobName in jobs.Select(job => job.Name))
        {
            if (!ExpectedIntervals.ContainsKey(jobName))
                throw new InvalidOperationException($"No heartbeat interval configured for '{jobName}'.");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BackgroundJobHeartbeats" ("JobName", "RegisteredAtUtc")
                VALUES ({jobName}, {registeredAtUtc}) ON CONFLICT ("JobName") DO NOTHING
                """, cancellationToken);
        }
    }

    public async Task RecordTick(string jobName, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            var tickAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BackgroundJobHeartbeats" ("JobName", "RegisteredAtUtc", "LastSuccessfulTickAtUtc")
                VALUES ({jobName}, {tickAtUtc}, {tickAtUtc})
                ON CONFLICT ("JobName") DO UPDATE SET "LastSuccessfulTickAtUtc" = EXCLUDED."LastSuccessfulTickAtUtc"
                """, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not persist heartbeat for {JobName}", jobName);
        }
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var rows = await db.BackgroundJobHeartbeats.AsNoTracking().ToDictionaryAsync(row => row.JobName, cancellationToken);
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        var stale = new List<string>();
        var statuses = new Dictionary<string, object>();
        foreach (var jobName in jobs.Select(job => job.Name))
        {
            if (!ExpectedIntervals.TryGetValue(jobName, out var interval))
                throw new InvalidOperationException($"No heartbeat interval configured for '{jobName}'.");
            if (!rows.TryGetValue(jobName, out var row))
            {
                stale.Add(jobName);
                statuses[jobName] = "Job registration missing";
                continue;
            }
            var elapsed = nowAtUtc - (row.LastSuccessfulTickAtUtc ?? row.RegisteredAtUtc);
            statuses[jobName] = row.LastSuccessfulTickAtUtc is null ? "No tick recorded yet" : $"Last tick: {elapsed.TotalMinutes:F0}m ago";
            if (elapsed > interval) stale.Add(jobName);
        }
        return stale.Count > 0
            ? HealthCheckResult.Unhealthy($"Stale services: {string.Join(", ", stale)}", data: statuses)
            : HealthCheckResult.Healthy("All background services running", statuses);
    }
}
