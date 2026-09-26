namespace Orbit.Infrastructure.BackgroundJobs;

public interface IScheduledJob
{
    string Name { get; }

    string CronExpression { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
