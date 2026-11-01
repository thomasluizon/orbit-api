using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orbit.Infrastructure.Services;

public sealed class AiUsageSummaryStartupService(
    AiUsageSummaryService summary, ILogger<AiUsageSummaryStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await summary.RunAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "AI startup cost summary could not be read");
        }
    }
}
