using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public sealed partial class FoundingAchievementReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<FoundingAchievementReconciliationService> logger,
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
    internal const int PageSize = 100;

    private readonly TimeSpan _interval = TimeSpan.FromHours(
        configuration.GetValue("BackgroundServices:AchievementReconciliationIntervalHours", 6));

    public string Name => "founding-achievement-reconciliation";

    public string CronExpression => "17 */6 * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        var grantedCount = await ReconcileCandidatesAsync(stoppingToken);
        if (grantedCount > 0)
            LogReconciliationCompleted(logger, grantedCount);

        BackgroundServiceHealthCheck.RecordTick("FoundingAchievementReconciliation");
    }

    internal async Task<int> ReconcileCandidatesAsync(CancellationToken cancellationToken)
    {
        FoundingAchievementCursor? cursor = null;
        var grantedCount = 0;

        while (true)
        {
            using var scope = scopeFactory.CreateScope();
            var reader = scope.ServiceProvider.GetRequiredService<IFoundingAchievementReader>();
            var gamificationService = scope.ServiceProvider.GetRequiredService<IGamificationService>();
            var candidates = await reader.ReadCandidatePageAsync(cursor, PageSize, cancellationToken);
            if (candidates.Count == 0)
                return grantedCount;

            foreach (var candidate in candidates)
            {
                try
                {
                    var granted = await gamificationService.ReconcileFoundingAchievementsAsync(
                        candidate.UserId,
                        cancellationToken);
                    grantedCount += granted.Count;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogCandidateFailed(logger, candidate.UserId, exception);
                }
            }

            cursor = candidates[^1];
            if (candidates.Count < PageSize)
                return grantedCount;
        }
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "FoundingAchievementReconciliationService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "FoundingAchievementReconciliationService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in founding achievement reconciliation")]
    private static partial void LogServiceError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Founding achievement reconciliation failed for user {UserId}")]
    private static partial void LogCandidateFailed(ILogger logger, Guid userId, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Reconciled {AchievementCount} founding achievements")]
    private static partial void LogReconciliationCompleted(ILogger logger, int achievementCount);
}
