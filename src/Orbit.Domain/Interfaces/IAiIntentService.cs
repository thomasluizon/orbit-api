using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IAiIntentService
{
    Task<Result<AiResponse>> SendWithToolsAsync(
        string userMessage,
        string systemPrompt,
        IReadOnlyList<object> toolDeclarations,
        byte[]? imageData = null,
        string? imageMimeType = null,
        IReadOnlyList<ChatHistoryMessage>? history = null,
        CancellationToken cancellationToken = default);

    Task<Result<AiResponse>> ContinueWithToolResultsAsync(
        AiConversationContext conversationContext,
        IReadOnlyList<AiToolCallResult> results,
        CancellationToken cancellationToken = default);
}
