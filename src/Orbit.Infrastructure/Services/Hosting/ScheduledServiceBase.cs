using Microsoft.Extensions.Hosting;

namespace Orbit.Infrastructure.Services.Hosting;

public abstract class ScheduledServiceBase : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected virtual TimeProvider DelayClock => TimeProvider.System;

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

                await Task.Delay(Interval, DelayClock, stoppingToken);
            }
        }
        finally
        {
            LogStopped();
        }
    }
}
