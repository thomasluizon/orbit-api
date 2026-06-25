using Microsoft.Extensions.Hosting;

namespace Orbit.Infrastructure.Services.Hosting;

/// <summary>
/// Templates the in-process scheduler loop shared by the periodic <see cref="BackgroundService"/>
/// schedulers: log started, then until cancellation run one <see cref="ExecuteTickAsync"/> guarded by
/// a non-cancellation catch that logs the tick error, delay by <see cref="Interval"/>, and log stopped
/// on exit. Each derived service supplies its own interval, per-tick work, and service-specific log
/// messages so timing, work, and logging output stay identical to the hand-rolled loops.
/// </summary>
public abstract class ScheduledServiceBase : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected abstract Task ExecuteTickAsync(CancellationToken stoppingToken);

    protected abstract void LogStarted();

    protected abstract void LogStopped();

    protected abstract void LogTickError(Exception ex);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ExecuteTickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTickError(ex);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        finally
        {
            LogStopped();
        }
    }
}
