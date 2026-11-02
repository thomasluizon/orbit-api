using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class BoundedReadStateBackfillHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<BoundedReadStateBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await TryRunAsync(stoppingToken)) return;
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    internal async Task<bool> TryRunAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BoundedReadStateBackfill>().RunAsync(ct);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Historical habit and XP state backfill failed and will retry");
            return false;
        }
    }
}
