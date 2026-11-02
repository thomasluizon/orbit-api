using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class ConversationObjectCleanup : Entity
{
    public Guid UserId { get; private set; }
    public Guid ConversationId { get; private set; }

    private ConversationObjectCleanup() { }

    public static ConversationObjectCleanup Create(Guid userId, Guid conversationId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user id is required.", nameof(userId));
        if (conversationId == Guid.Empty) throw new ArgumentException("A conversation id is required.", nameof(conversationId));
        return new ConversationObjectCleanup { UserId = userId, ConversationId = conversationId };
    }
}
