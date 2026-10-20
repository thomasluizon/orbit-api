namespace Orbit.Application.Chat;

public interface IConversationTurnRecorder
{
    bool TryRecord(ConversationTurnCapture turn);
}

public sealed record ConversationTurnCapture(
    Guid UserId,
    Guid TurnId,
    DateTime CreatedAtUtc,
    string UserContent,
    string? AssistantContent,
    string ToolCallsJson,
    bool UsedModel);
