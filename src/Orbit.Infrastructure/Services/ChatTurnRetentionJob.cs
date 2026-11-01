using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class ChatTurnRetentionJob(IServiceScopeFactory scopeFactory) : IScheduledJob
{
    public string Name => "chat-turn-retention";
    public string CronExpression => "15 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var cutoffAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-30);
        await work.ExecuteInTransactionAsync(async token =>
        {
            await db.ChatTurns.Where(turn => turn.ExpiresAtUtc < cutoffAtUtc).ExecuteDeleteAsync(token);
        }, cancellationToken);
    }
}
