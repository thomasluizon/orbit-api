using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
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

    private static void RejectUnansweredSelections(IReadOnlyList<AiToolCall> calls,
        Dictionary<string, AiToolCall?> resolvedCalls, ToolExecutionAccumulator results,
        Dictionary<string, ToolCallOutcome> outcomes)
    {
        foreach (var call in calls)
        {
            if (resolvedCalls[call.Id] is not { } resolved || SelectionForWrite(resolved, results) is not { } unanswered)
                continue;
            results.PendingSelection = unanswered;
            outcomes[call.Id] = SelectionRequiredOutcome(call);
            resolvedCalls[call.Id] = null;
        }
    }

    private static HabitSelectionCandidates? SelectionForWrite(AiToolCall call, ToolExecutionAccumulator results)
    {
        var ids = EffectiveWriteTargetIds(call, results).Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (results.PendingSelection is { } pending && pending.Candidates.Any(candidate => ids.Contains(candidate.Id)))
            return pending;
        return results.SelectionCandidates.FirstOrDefault(selection =>
            selection.Candidates.Any(candidate => ids.Contains(candidate.Id)));
    }

    private static IEnumerable<Guid> EffectiveWriteTargetIds(AiToolCall call, ToolExecutionAccumulator results)
    {
        if (!BulkNoChangesOutcome.Supports(call.Name) || call.Args.TryGetProperty("revised_items", out _))
            return ArgumentEntityIds(call.Args);
        var (filter, error) = call.Name switch
        {
            "bulk_update_habits" or "bulk_reschedule_habits" => BulkHabitToolArguments.ParseRequiredFilter(call.Args),
            "bulk_update_habit_emojis" => BulkUpdateHabitEmojisTool.ParseInferenceFilter(call.Args),
            _ => BulkHabitToolArguments.ParseActionFilter(call.Args)
        };
        return error is null && filter is not null
            ? BulkHabitSelection.Select(results.AllHabits, filter).Select(habit => habit.Id)
            : ArgumentEntityIds(call.Args);
    }

    private static ResponseCards SelectionQuestion(HabitSelectionCandidates selection, string? language, bool parts)
    {
        var text = string.IsNullOrWhiteSpace(selection.Reference)
            ? ErrorCopy.Resolve("chat.answer.selectHabit", LocaleHelper.IsPortuguese(language))
            : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                ErrorCopy.Resolve("chat.answer.selectHabitReference", LocaleHelper.IsPortuguese(language)), selection.Reference);
        return new ResponseCards(text, Parts: parts ? [new ChatTextPart(text)] : null) { ServerComposedText = true };
    }
}
