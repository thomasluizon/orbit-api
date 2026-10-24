using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public sealed class ChatTurnStore(IServiceScopeFactory scopes) : IChatTurnStore
{
    public async Task<(ChatTurn Turn, bool Claimed)> ClaimAsync(ChatTurn candidate, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var existing = await db.ChatTurns.AsNoTracking().SingleOrDefaultAsync(
            turn => turn.UserId == candidate.UserId && turn.KeyHash == candidate.KeyHash, cancellationToken);
        if (existing is not null)
            return (existing, false);
        db.ChatTurns.Add(candidate);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return (candidate, true);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            return (await db.ChatTurns.AsNoTracking().SingleAsync(
                turn => turn.UserId == candidate.UserId && turn.KeyHash == candidate.KeyHash, cancellationToken), false);
        }
    }

    public async Task<ChatTurn> ReadAsync(Guid userId, Guid turnId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrbitDbContext>().ChatTurns.AsNoTracking()
            .SingleAsync(turn => turn.UserId == userId && turn.Id == turnId, cancellationToken);
    }

    public async Task CompleteAsync(Guid userId, Guid turnId, string terminalEvent, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var turn = await db.ChatTurns.SingleAsync(turn => turn.UserId == userId && turn.Id == turnId, cancellationToken);
        turn.Complete(terminalEvent);
        await db.SaveChangesAsync(cancellationToken);
    }
}
