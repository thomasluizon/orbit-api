using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
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
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var cutoffAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-ConversationTurn.RetentionDays);
        await work.ExecuteInTransactionAsync(async token =>
        {
            await db.ChatTurns.Where(turn => turn.ExpiresAtUtc < cutoffAtUtc
                || db.ConversationTurns.Any(conversation => conversation.UserId == turn.UserId
                    && conversation.TurnId == turn.Id && conversation.CreatedAtUtc < cutoffAtUtc))
                .ExecuteDeleteAsync(token);
            await db.ConversationTurns.Where(turn => turn.CreatedAtUtc < cutoffAtUtc).ExecuteDeleteAsync(token);
            await db.ChatConversationTurns.Where(turn => turn.CreatedAtUtc < cutoffAtUtc).ExecuteDeleteAsync(token);
        }, cancellationToken);
    }
}
