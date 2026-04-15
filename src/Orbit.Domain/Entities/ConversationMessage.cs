using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// Allowed authored roles for a <see cref="ConversationMessage"/>.
/// </summary>
/// <remarks>
/// Persisted as a string column. The API layer rejects any client-supplied
/// role other than <see cref="User"/> on inbound writes — <see cref="Assistant"/>
/// rows are written exclusively by the chat handler itself, never on behalf
/// of the client. This blocks role-spoofing prompt injection.
/// </remarks>
public enum ConversationMessageRole
{
    User,
    Assistant
}

/// <summary>
/// A single message in a <see cref="Conversation"/>. The API never accepts
/// <c>system</c> roles from clients; system prompt construction is owned by
/// the server. See <see cref="ConversationMessageRole"/>.
/// </summary>
public class ConversationMessage : Entity
{
    public Guid ConversationId { get; private set; }
    public ConversationMessageRole Role { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    private ConversationMessage()
    {
    }

    public static ConversationMessage Create(Guid conversationId, ConversationMessageRole role, string content)
    {
        return new ConversationMessage
        {
            ConversationId = conversationId,
            Role = role,
            Content = content ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
