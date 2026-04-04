using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Orbit.Infrastructure.Services;

public class BackgroundServiceHealthCheck : IHealthCheck
{
    private static readonly ConcurrentDictionary<string, DateTime> LastSuccessfulTicks = new();

    private static readonly Dictionary<string, TimeSpan> ExpectedIntervals = new()
    {
        ["ReminderScheduler"] = TimeSpan.FromMinutes(3),
        ["GoalDeadlineNotification"] = TimeSpan.FromMinutes(90),
        ["SlipAlertScheduler"] = TimeSpan.FromMinutes(15),
        ["HabitDueDateAdvancement"] = TimeSpan.FromMinutes(90),
        ["AccountDeletion"] = TimeSpan.FromHours(72),
        ["SyncCleanup"] = TimeSpan.FromHours(48)
    };

    public static void RecordTick(string serviceName)
    {
        LastSuccessfulTicks[serviceName] = DateTime.UtcNow;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var unhealthy = new List<string>();
        var data = new Dictionary<string, object>();

        foreach (var (name, maxInterval) in ExpectedIntervals)
        {
            if (LastSuccessfulTicks.TryGetValue(name, out var lastTick))
            {
                var elapsed = DateTime.UtcNow - lastTick;
                data[name] = $"Last tick: {elapsed.TotalMinutes:F0}m ago";
                if (elapsed > maxInterval)
                    unhealthy.Add(name);
            }
            else
            {
                data[name] = "No tick recorded yet";
            }
        }

        if (unhealthy.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Stale services: {string.Join(", ", unhealthy)}", data: data));

        return Task.FromResult(HealthCheckResult.Healthy("All background services running", data));
    }
}
