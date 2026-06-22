namespace Orbit.Infrastructure.BackgroundJobs;

/// <summary>
/// A recurring background scan that can run either as an in-process polling loop or as a durable
/// Hangfire recurring job, selected by the <c>BackgroundServices:UseDurableQueue</c> flag. Each
/// scheduler exposes its stable <see cref="Name"/> (the Hangfire recurring-job id and lock key) and
/// the <see cref="CronExpression"/> that mirrors its default in-process interval, while
/// <see cref="RunAsync"/> performs one occurrence of the same work the polling loop runs each tick.
/// </summary>
public interface IScheduledJob
{
    string Name { get; }

    string CronExpression { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
