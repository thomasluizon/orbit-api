using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Reconciles achievement eligibility before free users can read the achievement payload. Failures retry
/// before startup, while feature-locked accounts are deferred to background retries until they unlock.
/// </summary>
public sealed partial class AchievementEligibilityReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IAchievementReconciliationState reconciliationState,
    ILogger<AchievementEligibilityReconciliationHostedService> logger) : IHostedService
{
    private const string CompletionKey = "AchievementEligibilityReconciliationComplete";
    private CancellationTokenSource? _retryCancellation;
    private Task? _retryTask;

    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    internal Task? DeferredRetryTask => _retryTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var status = await RunReconciliationAsync(cancellationToken);
        while (status == ReconciliationRunStatus.Failed)
        {
            await Task.Delay(RetryDelay, cancellationToken);
            status = await RunReconciliationAsync(cancellationToken);
        }

        if (status == ReconciliationRunStatus.Deferred)
        {
            _retryCancellation = new CancellationTokenSource();
            _retryTask = RetryDeferredAccountsAsync(_retryCancellation.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_retryCancellation is null || _retryTask is null)
            return;

        await _retryCancellation.CancelAsync();
        try
        {
            await _retryTask;
        }
        catch (OperationCanceledException) when (_retryCancellation.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            _retryCancellation.Dispose();
            _retryCancellation = null;
            _retryTask = null;
        }
    }

    internal async Task<ReconciliationRunStatus> RunReconciliationAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

            if (await db.AppConfigs.AnyAsync(config => config.Key == CompletionKey, stoppingToken))
            {
                reconciliationState.MarkComplete();
                LogAlreadyComplete(logger);
                return ReconciliationRunStatus.Complete;
            }

            var service = scope.ServiceProvider.GetRequiredService<IAchievementEligibilityReconciliationService>();
            var result = await service.ReconcileAllAsync(stoppingToken);
            if (result.AccountsDeferred > 0)
            {
                LogDeferred(logger, result.AccountsDeferred);
                return ReconciliationRunStatus.Deferred;
            }

            db.AppConfigs.Add(AppConfig.Create(
                CompletionKey,
                "true",
                "Set automatically after the one-time achievement eligibility reconciliation"));
            await db.SaveChangesAsync(stoppingToken);

            reconciliationState.MarkComplete();
            LogCompleted(logger, result.AccountsGranted, result.AchievementsGranted);
            return ReconciliationRunStatus.Complete;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
            return ReconciliationRunStatus.Failed;
        }
    }

    private async Task RetryDeferredAccountsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(RetryDelay, cancellationToken);
            var status = await RunReconciliationAsync(cancellationToken);
            if (status == ReconciliationRunStatus.Complete)
                return;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Achievement eligibility reconciliation already completed; skipping")]
    private static partial void LogAlreadyComplete(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Achievement eligibility reconciliation granted {AchievementCount} achievements across {AccountCount} accounts")]
    private static partial void LogCompleted(ILogger logger, int accountCount, int achievementCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Achievement eligibility reconciliation deferred for {AccountCount} feature-locked accounts")]
    private static partial void LogDeferred(ILogger logger, int accountCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Achievement eligibility reconciliation failed; retrying")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}

internal enum ReconciliationRunStatus
{
    Complete,
    Deferred,
    Failed
}
