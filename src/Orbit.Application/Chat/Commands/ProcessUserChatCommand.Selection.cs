using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Models;
using System.Text.Json;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool HasDeclaredReadPurpose(AiToolCall call, ProcessUserChatCommand request) =>
        call.Name != "query_habits" || request.ClientContext?.SupportsParts != true
        || request.ClientContext.PartKinds?.Any(IsAskKind) != true
        || call.Args.TryGetProperty("for_selection", out var selection) && selection.ValueKind == JsonValueKind.False;

    private static ToolCallOutcome SelectionRequiredOutcome(AiToolCall call) => new(
        new AiToolCallResult(call.Name, call.Id, false, null, null,
            "Selection is unanswered. Call ask with the returned candidates and prepare no write until the person answers."),
        null, null, null, null);

    private static HabitSelectionCandidates? SelectionForWrite(AiToolCall call, ToolExecutionAccumulator results)
    {
        if (results.PendingSelection is { } pending)
            return pending;
        var ids = ArgumentEntityIds(call.Args).Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return results.AmbiguousReadCandidates.FirstOrDefault(selection =>
            selection.Candidates.Any(candidate => ids.Contains(candidate.Id)));
    }

    private static ResponseCards SelectionQuestion(HabitSelectionCandidates selection, string? language, bool parts)
    {
        var text = LocaleHelper.IsPortuguese(language)
            ? "Qual hábito você quer escolher?" : "Which habit would you like to choose?";
        if (!string.IsNullOrWhiteSpace(selection.Reference))
            text = LocaleHelper.IsPortuguese(language)
                ? $"Qual hábito você quer usar para “{selection.Reference}”?"
                : $"Which habit would you like to use for “{selection.Reference}”?";
        return new ResponseCards(text, Parts: parts ? [new ChatTextPart(text)] : null) { ServerComposedText = true };
    }
}
