using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Keeps the diagnostic corpus for 30 days to allow failure reports to be investigated without
/// retaining a permanent copy of personal conversations. Expired rows are purged hourly.
/// </summary>
public sealed class ConversationRetentionJob(IServiceScopeFactory scopeFactory) : IScheduledJob
{
    public string Name => "conversation-retention";
    public string CronExpression => "15 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var cutoffAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-ConversationTurn.RetentionDays);
        await db.ConversationTurns.Where(turn => turn.CreatedAtUtc < cutoffAtUtc).ExecuteDeleteAsync(cancellationToken);
    }
}
