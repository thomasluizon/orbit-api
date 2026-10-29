using Orbit.Application.Chat.Grounding;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool CanRecoverReadAnswer(ProcessUserChatCommand request, ToolExecutionAccumulator results) =>
        request.BlockAnswer is null && results.AskBlock is null && !results.RequiresCreationProposal
        && results.PendingOperations.Count == 0 && results.ActionResults.Count == 0
        && results.OperationResults.Count > 0 && results.OperationResults.All(operation =>
            operation.Status == AgentOperationStatus.Succeeded && operation.ReadBlock is not null);

    private async Task<(ResponseCards Cards, IReadOnlyList<string>? FollowUps, TurnFactSet Facts)> RecoverEmptyReadReplyAsync(
        (ResponseCards Cards, IReadOnlyList<string>? FollowUps, TurnFactSet Facts) reply,
        ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results, CancellationToken ct)
    {
        if (reply.Cards.HasAnyCard || !string.IsNullOrWhiteSpace(reply.Cards.AiMessage) || !CanRecoverReadAnswer(request, results))
            return reply;
        var response = await RecoverReadAnswerAsync(request, context, results, ct);
        return (new ResponseCards(response.AiMessage, Parts: response.Parts) { ServerComposedText = true }, null, response.GroundingFacts);
    }

    private async Task<ChatResponse> RecoverReadAnswerAsync(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cards = ComposeAnswer(FollowUpDirective.Parse(null), false, context.Language, request, context, results);
        var response = ChatResponseNegotiation.ForClient(new ChatResponse(cards.AiMessage, [], results.OperationResults,
            CorrelationId: request.CorrelationId, RelatedSurfaces: results.RelatedSurfaces, Parts: cards.Parts)
        { GroundingFacts = TurnFactSet.FromToolResults(results.ToolResults) }, request.ClientContext,
            context.EnabledFeatureFlags, context.Language, context.User?.TimeZone, context.UserToday);
        CaptureResponseCardEvents(request, context, cards);
        if (request.StreamSink is { } sink)
        {
            await sink(ChatStreamEvent.Reset());
            if (!string.IsNullOrWhiteSpace(response.AiMessage))
                await sink(ChatStreamEvent.Delta(response.AiMessage));
        }
        return response;
    }
}
