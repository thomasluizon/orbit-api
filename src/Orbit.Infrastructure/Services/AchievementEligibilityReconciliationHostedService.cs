using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Runs the one-time achievement eligibility reconciliation after startup. The completion marker is
/// written only after the free-tier flag is available to free users and the full sweep succeeds.
/// </summary>
public sealed partial class AchievementEligibilityReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<AchievementEligibilityReconciliationHostedService> logger) : BackgroundService
{
    private const string CompletionKey = "AchievementEligibilityReconciliationComplete";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        await RunReconciliationAsync(stoppingToken);
    }

    internal async Task RunReconciliationAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

            if (await db.AppConfigs.AnyAsync(config => config.Key == CompletionKey, stoppingToken))
            {
                LogAlreadyComplete(logger);
                return;
            }

            var freeTierFlag = await db.AppFeatureFlags
                .AsNoTracking()
                .FirstOrDefaultAsync(flag => flag.Key == FeatureFlagKeys.GamificationFreeTier, stoppingToken);
            if (!IsAvailableToFreeUsers(freeTierFlag))
            {
                LogFreeTierUnavailable(logger);
                return;
            }

            var service = scope.ServiceProvider.GetRequiredService<IAchievementEligibilityReconciliationService>();
            var result = await service.ReconcileAllAsync(stoppingToken);

            db.AppConfigs.Add(AppConfig.Create(
                CompletionKey,
                "true",
                "Set automatically after the one-time achievement eligibility reconciliation"));
            await db.SaveChangesAsync(stoppingToken);

            LogCompleted(logger, result.AccountsGranted, result.AchievementsGranted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
        }
    }

    private static bool IsAvailableToFreeUsers(AppFeatureFlag? flag)
    {
        if (flag is not { Enabled: true })
            return false;

        return string.IsNullOrWhiteSpace(flag.PlanRequirement)
            || string.Equals(flag.PlanRequirement.Trim(), "free", StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Achievement eligibility reconciliation already completed; skipping")]
    private static partial void LogAlreadyComplete(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Achievement eligibility reconciliation deferred because the free-tier flag is unavailable")]
    private static partial void LogFreeTierUnavailable(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Achievement eligibility reconciliation granted {AchievementCount} achievements across {AccountCount} accounts")]
    private static partial void LogCompleted(ILogger logger, int accountCount, int achievementCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Achievement eligibility reconciliation failed and will retry on next startup")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
