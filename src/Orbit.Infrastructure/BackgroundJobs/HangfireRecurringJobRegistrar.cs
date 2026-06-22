using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orbit.Infrastructure.BackgroundJobs;

/// <summary>
/// Registers every <see cref="IScheduledJob"/> as a Hangfire recurring job on startup when the
/// durable-queue flag is on. Each job is keyed by its stable name, so re-registering on every boot
/// reconciles cron changes without creating duplicates, and Hangfire's storage keeps the schedule
/// across restarts while its distributed lock ensures a single instance runs each occurrence.
/// </summary>
public sealed partial class HangfireRecurringJobRegistrar(
    IRecurringJobManager recurringJobManager,
    IEnumerable<IScheduledJob> jobs,
    ILogger<HangfireRecurringJobRegistrar> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var job in jobs)
        {
            recurringJobManager.AddOrUpdate<ScheduledJobRunner>(
                job.Name,
                runner => runner.RunAsync(job.Name, CancellationToken.None),
                job.CronExpression);

            LogJobRegistered(logger, job.Name, job.CronExpression);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Registered durable recurring job {JobName} with schedule {CronExpression}")]
    private static partial void LogJobRegistered(ILogger logger, string jobName, string cronExpression);
}
