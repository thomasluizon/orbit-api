namespace Orbit.Infrastructure.BackgroundJobs;

/// <summary>
/// Single Hangfire entry point for every <see cref="IScheduledJob"/>. Hangfire persists only the
/// job's <see cref="IScheduledJob.Name"/> in storage and resolves a fresh runner per execution, so
/// adding or renaming a job never changes the serialized recurring-job payload. The runner looks up
/// the matching job by name and executes one occurrence of its work.
/// </summary>
public sealed class ScheduledJobRunner(IEnumerable<IScheduledJob> jobs)
{
    public Task RunAsync(string jobName, CancellationToken cancellationToken)
    {
        var job = jobs.FirstOrDefault(candidate => candidate.Name == jobName)
            ?? throw new InvalidOperationException($"No scheduled job registered with name '{jobName}'.");

        return job.RunAsync(cancellationToken);
    }
}
