using System.Text.Json.Serialization;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static ResponseCards SuppressCreationReadCards(ResponseCards cards, ProcessUserChatCommand request,
        ToolExecutionAccumulator results)
    {
        if (HasCombinedCreationPreview(results))
            return new ResponseCards(null, Parts: request.ClientContext?.SupportsParts == true ? [] : null)
            { ServerComposedText = true };
        return HasPendingWritePreview(results) ? cards with
        {
            HabitList = cards.HabitList is { Items.Count: 0 } ? null : cards.HabitList,
            GoalList = cards.GoalList is { Items.Count: 0 } ? null : cards.GoalList,
            RecordLists = cards.RecordLists?.Where(card => card.Items.Count > 0).ToArray(),
            Parts = cards.Parts?.Where(part => part is not ChatBlockPart
            {
                Block: HabitListCard { Items.Count: 0 }
                or GoalListCard { Items.Count: 0 } or RecordListCard { Items.Count: 0 }
            }).ToArray()
        } : cards;
    }

    private static bool HasCombinedCreationPreview(ToolExecutionAccumulator results) =>
        results.PendingOperations is [var pending]
        && results.PendingWriteCalls.TryGetValue(pending.Id, out var held) && held.ToolName == "bulk_create_habits"
        && results.ActionResults.Count == 0 && results.AskBlock is null && SuppressPreviewNarrative(results);

    private static void CompleteCreationProposal(AiResponse response,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes, ToolExecutionAccumulator results)
    {
        if (TerminalWriteDirective.IsComplete(response)
            && response.ToolCalls!.Any(call => call.Name == "bulk_create_habits"
                && outcomes.TryGetValue(call.Id, out var outcome)
                && outcome.PendingOperation is { ChangeTargetCount: > 0, Items.Count: > 0 }))
            results.RequiresCreationProposal = false;
    }

    private static ToolCallOutcome AwaitCreationReads(AiToolCall call) => new(
        new AiToolCallResult(call.Name, call.Id, true, null, null, null,
            new CreationReadDependency("awaiting_reads",
                "Nothing was previewed or saved. Read the results from this round first, then call bulk_create_habits once with the complete set. Reuse each existing habit's ID and stored title verbatim. Copy selected suggestion draft fields unchanged, without inventing quantities, times or activities.")),
        null, null, null, null);

    private sealed record CreationReadDependency(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("instruction")] string Instruction);
}
