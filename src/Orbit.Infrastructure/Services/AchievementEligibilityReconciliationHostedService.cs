using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Gamification.Backfill;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Reconciles historical achievement eligibility in bounded background passes. Startup only launches
/// the worker; failures, remaining pages, and feature-locked accounts retry without delaying readiness.
/// </summary>
public sealed partial class AchievementEligibilityReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<AchievementEligibilityReconciliationHostedService> logger) : IHostedService
{
    private CancellationTokenSource? _workerCancellation;
    private Task? _workerTask;

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    internal Task? WorkerTask => _workerTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workerCancellation = new CancellationTokenSource();
        _workerTask = Task.Run(
            () => RunWorkerAsync(_workerCancellation.Token),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_workerCancellation is null || _workerTask is null)
            return;

        await _workerCancellation.CancelAsync();
        try
        {
            await _workerTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (_workerCancellation.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            _workerCancellation.Dispose();
            _workerCancellation = null;
            _workerTask = null;
        }
    }

    internal async Task<ReconciliationRunStatus> RunReconciliationAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IAchievementEligibilityReconciliationService>();
            var result = await service.ReconcileAllAsync(stoppingToken);
            if (result.AchievementsGranted > 0)
                LogCompleted(logger, result.AccountsGranted, result.AchievementsGranted);

            if (result.AccountsDeferred > 0)
            {
                LogDeferred(logger, result.AccountsDeferred);
                return ReconciliationRunStatus.Deferred;
            }

            if (result.HasMoreCandidates)
                return ReconciliationRunStatus.Pending;

            return ReconciliationRunStatus.Complete;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
            return ReconciliationRunStatus.Failed;
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await RunReconciliationAsync(cancellationToken);
            await Task.Delay(RetryDelay, cancellationToken);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Achievement eligibility reconciliation granted {AchievementCount} achievements across {AccountCount} accounts")]
    private static partial void LogCompleted(ILogger logger, int accountCount, int achievementCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Achievement eligibility reconciliation deferred for {AccountCount} feature-locked accounts")]
    private static partial void LogDeferred(ILogger logger, int accountCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Achievement eligibility reconciliation failed; retrying")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}

internal enum ReconciliationRunStatus
{
    Complete,
    Deferred,
    Pending,
    Failed
}
