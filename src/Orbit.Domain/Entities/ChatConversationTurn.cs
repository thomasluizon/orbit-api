using System.Text.Json;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class ChatConversationTurn : Entity
{
    public const int MaxDataLength = 32_000_000;
    public Guid UserId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid TurnId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool HasAttachment { get; private set; }
    public bool HasPreview { get; private set; }
    public string DataJson { get; private set; } = "{}";

    private ChatConversationTurn() { }

    public static Result<ChatConversationTurn> Create(Guid userId, Guid conversationId, Guid turnId,
        DateTime createdAtUtc, string dataJson, bool hasAttachment, bool hasPreview)
    {
        if (userId == Guid.Empty || conversationId == Guid.Empty || turnId == Guid.Empty
            || createdAtUtc == default || createdAtUtc.Kind != DateTimeKind.Utc)
            return Result.Failure<ChatConversationTurn>("Conversation identity and a UTC timestamp are required.");
        if (string.IsNullOrWhiteSpace(dataJson) || dataJson.Length > MaxDataLength)
            return Result.Failure<ChatConversationTurn>("Conversation data exceeds the storage limit.");
        try
        {
            using var document = JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Result.Failure<ChatConversationTurn>("Conversation data must be an object.");
        }
        catch (JsonException)
        {
            return Result.Failure<ChatConversationTurn>("Conversation data must be valid JSON.");
        }
        return Result.Success(new ChatConversationTurn
        {
            UserId = userId,
            ConversationId = conversationId,
            TurnId = turnId,
            CreatedAtUtc = createdAtUtc,
            DataJson = dataJson,
            HasAttachment = hasAttachment,
            HasPreview = hasPreview
        });
    }
}
