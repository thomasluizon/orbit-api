using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IConversationOfferStore
{
    Task<IReadOnlySet<string>> DismissedAsync(Guid userId, Guid conversationId, CancellationToken ct);
    Task<IReadOnlyList<AiOfferFact>> IssuedAsync(Guid userId, Guid conversationId, CancellationToken ct);
    Task<AiOfferFact?> FindIssuedAsync(Guid userId, Guid conversationId, string key, CancellationToken ct);
    Task IssueAsync(Guid userId, Guid conversationId, AiOfferFact fact, CancellationToken ct);
    Task DismissAsync(Guid userId, Guid conversationId, IReadOnlyList<string> keys, CancellationToken ct);
}
