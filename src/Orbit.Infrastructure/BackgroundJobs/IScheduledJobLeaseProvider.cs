namespace Orbit.Infrastructure.BackgroundJobs;

public interface IScheduledJobLeaseProvider
{
    Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken);
}
