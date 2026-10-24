using Orbit.Domain.Entities;

namespace Orbit.Application.Chat;

public interface IChatTurnStore
{
    Task<(ChatTurn Turn, bool Claimed)> ClaimAsync(ChatTurn candidate, CancellationToken cancellationToken);
    Task<ChatTurn> ReadAsync(Guid userId, Guid turnId, CancellationToken cancellationToken);
    Task CompleteAsync(Guid userId, Guid turnId, string terminalEvent, CancellationToken cancellationToken);
}
