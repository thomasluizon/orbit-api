using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IAiIntentService
{
    Task<Result<AiResponse>> SendWithToolsAsync(
        AiToolRequest request,
        Func<AiStreamEvent, Task>? streamSink = null,
        CancellationToken cancellationToken = default);

    Task<Result<AiResponse>> ContinueWithToolResultsAsync(
        AiConversationContext conversationContext,
        IReadOnlyList<AiToolCallResult> results,
        Func<AiStreamEvent, Task>? streamSink = null,
        CancellationToken cancellationToken = default);
}
