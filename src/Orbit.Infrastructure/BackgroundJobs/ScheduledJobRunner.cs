using Microsoft.Extensions.Configuration;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.BackgroundJobs;

public sealed class ScheduledJobRunner(
    IEnumerable<IScheduledJob> jobs,
    IScheduledJobLeaseProvider leases,
    BackgroundServiceHealthCheck health,
    IConfiguration configuration,
    TimeProvider? clock = null)
{
    public async Task RunAsync(string jobName, CancellationToken cancellationToken)
    {
        var job = jobs.FirstOrDefault(candidate => candidate.Name == jobName)
            ?? throw new InvalidOperationException($"No scheduled job registered with name '{jobName}'.");
        await using var lease = await leases.TryAcquireAsync(jobName, cancellationToken);
        if (lease is null) return;
        using var deadline = AiCallDeadline.Start(
            TimeSpan.FromMinutes(configuration.GetValue($"BackgroundServices:TimeoutMinutes:{jobName}", 120)),
            clock ?? TimeProvider.System, cancellationToken);
        await job.RunAsync(deadline.Token);
        await health.RecordTick(jobName, cancellationToken);
    }
}
