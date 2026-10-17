using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services.Notion;

public sealed class NotionAutoSyncService(IServiceScopeFactory scopeFactory, TimeProvider clock,
    ILogger<NotionAutoSyncService> logger) : BackgroundService, IScheduledJob
{
    public string Name => "notion-auto-sync";
    public string CronExpression => "*/15 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var cutoff = nowUtc.AddMinutes(-15);
        var userIds = await db.NotionConnections.AsNoTracking()
            .Where(c => c.Status != NotionConnectionStatus.ReconnectRequired && (c.LastPollAtUtc == null || c.LastPollAtUtc <= cutoff))
            .Join(db.Users.Where(u => u.IsLifetimePro || (u.Plan == UserPlan.Pro && u.PlanExpiresAt > nowUtc) || u.TrialEndsAt > nowUtc),
                c => c.UserId, u => u.Id, (c, u) => c.UserId).ToListAsync(cancellationToken);
        foreach (var userId in userIds)
        {
            using var userScope = scopeFactory.CreateScope();
            try { await userScope.ServiceProvider.GetRequiredService<NotionPollService>().PollAsync(userId, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notion sync could not finish for {UserId}", userId);
            }
        }
        BackgroundServiceHealthCheck.RecordTick("NotionAutoSync");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notion sync tick could not finish");
            }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
