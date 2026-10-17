using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Proactive.Services;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public sealed partial class ProactiveEngineSchedulerService(
    IServiceScopeFactory scopeFactory, ILogger<ProactiveEngineSchedulerService> logger)
    : ScheduledServiceBase, IScheduledJob
{
    public string Name => "proactive-checkin-scheduler";
    public string CronExpression => "0 * * * *";
    protected override TimeSpan Interval => TimeSpan.FromHours(1);
    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProactiveEngine>().EvaluateAsync(stoppingToken);
        BackgroundServiceHealthCheck.RecordTick("ProactiveCheckinScheduler");
    }

    protected override void LogStarted() => Started(logger);
    protected override void LogStopped() => Stopped(logger);
    protected override void LogTickError(Exception ex) => Failed(logger, ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proactive engine started")]
    private static partial void Started(ILogger logger);
    [LoggerMessage(Level = LogLevel.Information, Message = "Proactive engine stopped")]
    private static partial void Stopped(ILogger logger);
    [LoggerMessage(Level = LogLevel.Error, Message = "Proactive engine evaluation failed")]
    private static partial void Failed(ILogger logger, Exception exception);
}
