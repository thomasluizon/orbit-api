using Orbit.Application.Chat.Grounding;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<(ResponseCards Cards, IReadOnlyList<string>? FollowUps, TurnFactSet Facts)> ComposeFinalReplyAsync(
        ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results,
        ToolLoopResult loop, AiResponse response, GroundingTurn grounding, CancellationToken ct)
    {
        using var phase = ChatTurnTimer.Measure(ChatPhase.Composition);
        if (BulkNoChangesMessage(results, context.Language) is not { } message)
        {
            var reply = await ComposeRegularReplyAsync(request, context, results, loop, response, grounding, ct);
            return await RecoverEmptyReadReplyAsync(reply, request, context, results, ct);
        }
        if (request.StreamSink is not null) await request.StreamSink(ChatStreamEvent.Reset());
        return (new ResponseCards(message,
            Parts: request.ClientContext?.SupportsParts == true ? [new ChatTextPart(message)] : null)
        { ServerComposedText = true }, null, TurnFactSet.FromToolResults(results.ToolResults));
    }

    private string? BulkNoChangesMessage(ToolExecutionAccumulator results, string? language)
    {
        if (HasResolutionQuestion(results) || results.PendingOperations.Count > 0 || results.ActionResults.Count > 0 || results.AskBlock is not null
            || results.OperationResults.Any(operation => operation.Status != AgentOperationStatus.Succeeded
                || operation.Payload is not BulkNoChangesOutcome
                && ai.ToolRegistry.GetTool(operation.OperationId) is not { IsReadOnly: true }))
            return null;
        var outcomes = results.OperationResults.Select(operation => operation.Payload).OfType<BulkNoChangesOutcome>().ToArray();
        return outcomes.Length == 0 ? null
            : new BulkNoChangesOutcome(outcomes.All(outcome => outcome.IconsAlreadyPresent)).Message(language);
    }
}
