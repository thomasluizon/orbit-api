using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public interface IChatConversationStore
{
    Task<ChatConversationMemory> LoadAsync(Guid userId, Guid conversationId, CancellationToken ct);
    Task SaveAsync(ChatConversationCapture capture, CancellationToken ct);
}

public sealed record ChatConversationCapture(Guid UserId, Guid ConversationId, Guid TurnId, DateTime CreatedAtUtc,
    string UserMessage, string? AssistantMessage, byte[]? ImageData, string? ImageMimeType,
    IReadOnlyList<PendingAgentOperation> Previews);

public sealed record ChatConversationPreview(PendingAgentOperation Proposal, string Outcome, bool WasEdited,
    JsonElement? CurrentArguments);

public sealed record ChatConversationMemory(IReadOnlyList<ChatHistoryMessage> History,
    IReadOnlyList<AiConversationAttachment> Attachments, IReadOnlyList<ChatConversationPreview> LastPreviews);
