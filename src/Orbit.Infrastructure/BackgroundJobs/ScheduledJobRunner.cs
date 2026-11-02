using Microsoft.Extensions.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.BackgroundJobs;

public sealed class ScheduledJobRunner(
    IEnumerable<IScheduledJob> jobs,
    IScheduledJobLeaseProvider leases,
    BackgroundServiceHealthCheck health,
    IConfiguration configuration)
{
    public async Task RunAsync(string jobName, CancellationToken cancellationToken)
    {
        var job = jobs.FirstOrDefault(candidate => candidate.Name == jobName)
            ?? throw new InvalidOperationException($"No scheduled job registered with name '{jobName}'.");
        await using var lease = await leases.TryAcquireAsync(jobName, cancellationToken);
        if (lease is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(configuration.GetValue($"BackgroundServices:TimeoutMinutes:{jobName}", 120)));
        await job.RunAsync(deadline.Token);
        await health.RecordTick(jobName, cancellationToken);
    }
}
