using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// A chat conversation between a user and the AI assistant. Each message
/// turn is appended as a <see cref="ConversationMessage"/>.
/// </summary>
/// <remarks>
/// Server-authoritative chat history (PLAN.md F4 / Frontend Area B #3 /
/// backend P0 #5). Replaces the previous design where the client posted
/// the entire <c>history</c> blob with each request — a malicious client
/// could forge prior <c>system</c>/<c>assistant</c> turns to bypass safety
/// rails. With server-stored conversations the client only sends a
/// <c>conversationId</c>, and the API loads the last N turns from this table.
/// </remarks>
public class Conversation : Entity, ITimestamped
{
    public Guid UserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime LastMessageAtUtc { get; private set; }

    private Conversation()
    {
    }

    public static Conversation Create(Guid userId)
    {
        var now = DateTime.UtcNow;
        return new Conversation
        {
            UserId = userId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastMessageAtUtc = now
        };
    }

    public void TouchLastMessage()
    {
        LastMessageAtUtc = DateTime.UtcNow;
    }
}
