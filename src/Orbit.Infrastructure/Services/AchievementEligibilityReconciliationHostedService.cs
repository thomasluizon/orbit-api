using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Completes the one-time achievement eligibility reconciliation before the host accepts requests.
/// Failed attempts retry in-process, and the completion marker is written only after a full sweep.
/// </summary>
public sealed partial class AchievementEligibilityReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<AchievementEligibilityReconciliationHostedService> logger) : IHostedService
{
    private const string CompletionKey = "AchievementEligibilityReconciliationComplete";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (!await RunReconciliationAsync(cancellationToken))
            await Task.Delay(RetryDelay, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task<bool> RunReconciliationAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

            if (await db.AppConfigs.AnyAsync(config => config.Key == CompletionKey, stoppingToken))
            {
                LogAlreadyComplete(logger);
                return true;
            }

            var service = scope.ServiceProvider.GetRequiredService<IAchievementEligibilityReconciliationService>();
            var result = await service.ReconcileAllAsync(stoppingToken);

            db.AppConfigs.Add(AppConfig.Create(
                CompletionKey,
                "true",
                "Set automatically after the one-time achievement eligibility reconciliation"));
            await db.SaveChangesAsync(stoppingToken);

            LogCompleted(logger, result.AccountsGranted, result.AchievementsGranted);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
            return false;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Achievement eligibility reconciliation already completed; skipping")]
    private static partial void LogAlreadyComplete(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Achievement eligibility reconciliation granted {AchievementCount} achievements across {AccountCount} accounts")]
    private static partial void LogCompleted(ILogger logger, int accountCount, int achievementCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Achievement eligibility reconciliation failed; retrying before startup completes")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
