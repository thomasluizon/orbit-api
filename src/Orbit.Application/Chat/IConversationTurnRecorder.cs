using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public interface IConversationTurnRecorder
{
    Task<bool> RecordAsync(ConversationTurnCapture turn, CancellationToken cancellationToken = default);
    Task<Result<StoredConversationHistory>> LoadHistoryAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default);
    Task<ConversationPage?> GetLatestAsync(Guid userId, Guid? conversationId, long? beforeOrder, int pageSize, CancellationToken cancellationToken = default);
    Task<StoredConversationImage?> ReadImageAsync(Guid userId, Guid conversationId, Guid turnId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default);
}

public sealed record StoredConversationHistory(bool Exists, IReadOnlyList<ChatHistoryMessage> Messages);

public sealed record ConversationPage(Guid ConversationId, IReadOnlyList<ConversationDisplayTurn> Turns, long? BeforeOrder);

public sealed record ConversationDisplayTurn(Guid TurnId, long Order, string Role, DateTime CreatedAtUtc,
    string? Content, string? ResponseJson, string PreviewOutcomesJson, ConversationImageReference? Image);

public sealed record ConversationImageReference(string FileName, string ContentType, string Sha256, string? Url = null);
public sealed record StoredConversationImage(byte[] Data, string ContentType);

public sealed record ConversationTurnCapture(
    Guid UserId,
    Guid TurnId,
    DateTime CreatedAtUtc,
    string UserContent,
    string? AssistantContent,
    string ToolCallsJson,
    bool UsedModel,
    string? ResponseJson = null,
    Guid? ConversationId = null,
    bool ExistingConversation = false,
    IReadOnlyList<AiToolCallResult>? ToolResults = null,
    byte[]? ImageData = null,
    string? ImageMimeType = null);
