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

    /// <summary>
    /// A name search that matches several habits is not ambiguous when the message names every match by its
    /// complete title, and no match title is part of another match title. That read selects nothing, so a write
    /// over the named habits needs no ask. A read that the model declares with for_selection=true still asks.
    /// </summary>
    private static AgentOperationResult ResolveNamedSelection(AiToolCall call, AgentOperationResult operation, string message)
    {
        if (call.Name != "query_habits" || IsSelectionRead(call)
            || operation.Payload is not HabitListCard { Selection: { } selection } card
            || card.TotalCount != selection.Candidates.Count || !NamesEveryCandidate(message, selection.Candidates))
            return operation;
        return operation with { Payload = card with { Selection = null } };
    }

    private static bool NamesEveryCandidate(string message, IReadOnlyList<HabitListCardItem> candidates)
    {
        var text = $" {HabitNameMatcher.Normalize(message)} ";
        var titles = candidates.Select(candidate => $" {HabitNameMatcher.Normalize(candidate.Title)} ").ToArray();
        return titles.Length > 1 && titles.Distinct(StringComparer.Ordinal).Count() == titles.Length
            && titles.All(title => title.Trim().Length > 0 && text.Contains(title, StringComparison.Ordinal)
                && titles.All(other => other == title || !other.Contains(title, StringComparison.Ordinal)));
    }

    private static ToolCallOutcome SelectionRequiredOutcome(AiToolCall call) => new(
        new AiToolCallResult(call.Name, call.Id, false, null, null,
            "Selection is unanswered. Call ask with the returned candidates and prepare no write until the person answers. A request that names the whole set or members by order needs no question, and writes that together cover every candidate exactly once in one round are accepted."),
        null, null, null, null);

    private static void RejectUnansweredSelections(IReadOnlyList<AiToolCall> calls,
        Dictionary<string, AiToolCall?> resolvedCalls, ToolExecutionAccumulator results,
        Dictionary<string, ToolCallOutcome> outcomes, SelectionScope scope)
    {
        var settled = new List<HabitSelectionCandidates>();
        var held = new List<HabitSelectionCandidates>();
        foreach (var call in calls)
        {
            if (resolvedCalls[call.Id] is not { } resolved)
                continue;
            var verdict = SelectionForWrite(resolved, results, scope);
            settled.AddRange(verdict.Settled);
            if (verdict.Unanswered is not { } unanswered)
                continue;
            held.Add(unanswered);
            results.PendingSelection = unanswered;
            outcomes[call.Id] = SelectionRequiredOutcome(call);
            resolvedCalls[call.Id] = null;
        }
        SettleSelections(results, settled.Where(selection => !held.Contains(selection, ReferenceEqualityComparer.Instance)));
    }

    private static void SettleSelections(ToolExecutionAccumulator results, IEnumerable<HabitSelectionCandidates> settled)
    {
        var done = settled.ToArray();
        if (done.Length == 0)
            return;
        results.SelectionCandidates.RemoveAll(selection => done.Contains(selection, ReferenceEqualityComparer.Instance));
        if (results.PendingSelection is { } pending && done.Contains(pending, ReferenceEqualityComparer.Instance))
            results.PendingSelection = results.SelectionCandidates.FirstOrDefault(selection => selection.RequiresAnswer);
    }

    private static void RejectUnreadDuplicateTargets(IReadOnlyList<AiToolCall> calls,
        Dictionary<string, AiToolCall?> resolvedCalls, ToolExecutionAccumulator results,
        Dictionary<string, ToolCallOutcome> outcomes)
    {
        foreach (var call in calls)
        {
            if (resolvedCalls[call.Id] is not { } resolved)
                continue;
            var ids = DuplicateWriteTargetIds(resolved, results).ToHashSet();
            var duplicateTargets = results.AllHabits.Where(habit => ids.Contains(habit.Id))
                .GroupBy(habit => HabitNameMatcher.Normalize(habit.Title)).Where(group => group.Count() > 1);
            if (!duplicateTargets.Any(group => group.Any(habit => !results.ReadHabitItems.ContainsKey(habit.Id.ToString()))))
                continue;
            outcomes[call.Id] = new ToolCallOutcome(new AiToolCallResult(call.Name, call.Id, false, null, null,
                "Several targets share a title and have not been read in this turn. Call query_habits to resolve the requested scope. A singular name requires for_selection=true and ask with the returned IDs; do not pass every matching ID to a bulk write. An explicit request for the full set uses for_selection=false without a name search."),
                null, null, null, null);
            resolvedCalls[call.Id] = null;
        }
    }

    private static IEnumerable<Guid> DuplicateWriteTargetIds(AiToolCall call, ToolExecutionAccumulator results)
    {
        if (call.Args.TryGetProperty("revised_items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("habit_id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => Guid.TryParse(item.GetProperty("habit_id").GetString(), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty);
        if (call.Args.TryGetProperty("habit_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            return ids.EnumerateArray().Where(id => id.ValueKind == JsonValueKind.String)
                .Select(id => Guid.TryParse(id.GetString(), out var parsed) ? parsed : Guid.Empty)
                .Where(id => id != Guid.Empty);
        return BulkNoChangesOutcome.Supports(call.Name) ? EffectiveWriteTargetIds(call, results) : [];
    }

    /// <summary>
    /// The writes of a turn and the request message. A selection that a write touches is settled, and the write may
    /// run, when the person answered it, when the message names the whole set or members by order, or when the
    /// turn's writes split one group of same-titled records between them without a gap or an overlap.
    /// </summary>
    private sealed record SelectionScope(string Message, IReadOnlyList<AiToolCall> TurnWrites);

    private sealed record SelectionVerdict(HabitSelectionCandidates? Unanswered, IReadOnlyList<HabitSelectionCandidates> Settled);

    private static SelectionScope SelectionScopeOf(ProcessUserChatCommand request,
        Dictionary<string, AiToolCall?> resolvedCalls, ToolExecutionAccumulator results) =>
        new(request.Message, [.. resolvedCalls.Values.OfType<AiToolCall>(), .. results.PreviousWriteCalls]);

    private static SelectionVerdict SelectionForWrite(AiToolCall call, ToolExecutionAccumulator results, SelectionScope scope)
    {
        var targets = EffectiveWriteTargetIds(call, results).ToHashSet();
        var unanswered = targets.Where(id => !results.AnsweredTargetIds.Contains(id))
            .Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settled = new List<HabitSelectionCandidates>();
        var selections = new[] { results.PendingSelection }.Concat(results.SelectionCandidates).OfType<HabitSelectionCandidates>()
            .Distinct(ReferenceEqualityComparer.Instance).Cast<HabitSelectionCandidates>();
        foreach (var selection in selections)
        {
            var ids = targets.Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!selection.Candidates.Any(candidate => ids.Contains(candidate.Id)))
                continue;
            var touched = selection.Candidates.Where(candidate => unanswered.Contains(candidate.Id))
                .GroupBy(candidate => HabitNameMatcher.Normalize(candidate.Title)).Select(group => group.Key).ToHashSet();
            var groups = selection.Candidates.GroupBy(candidate => HabitNameMatcher.Normalize(candidate.Title))
                .Where(group => touched.Contains(group.Key));
            if (groups.All(group => GroupIsSettled(group.Select(candidate => candidate.Id).ToArray(), scope, results)))
                settled.Add(selection);
            else
                return new SelectionVerdict(selection, settled);
        }
        return new SelectionVerdict(null, settled);
    }

    private static bool GroupIsSettled(IReadOnlyList<string> members, SelectionScope scope, ToolExecutionAccumulator results)
    {
        if (HabitSetRequestScope.NamesMembersByOrder(scope.Message))
            return true;
        var writes = scope.TurnWrites
            .Select(write => EffectiveWriteTargetIds(write, results).Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Where(targets => members.Any(targets.Contains)).ToArray();
        var hits = members.Select(member => writes.Count(targets => targets.Contains(member))).ToArray();
        if (HabitSetRequestScope.NamesWholeSet(scope.Message) && hits.All(count => count >= 1))
            return true;
        return writes.Length >= 2 && hits.All(count => count == 1);
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
        var reference = SharedTitle(selection) ?? selection.Reference;
        var text = string.IsNullOrWhiteSpace(reference)
            ? ErrorCopy.Resolve("chat.answer.selectHabit", LocaleHelper.IsPortuguese(language))
            : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                ErrorCopy.Resolve("chat.answer.selectHabitReference", LocaleHelper.IsPortuguese(language)), reference);
        return new ResponseCards(text, Parts: parts ? [new ChatTextPart(text)] : null) { ServerComposedText = true };
    }

    /// <summary>
    /// The stored title that every candidate shares. The question quotes it instead of the raw search text, so a
    /// title with punctuation reads as the person sees it in the list.
    /// </summary>
    private static string? SharedTitle(HabitSelectionCandidates selection) =>
        selection.Candidates.Count > 0 && selection.Candidates.Select(candidate => HabitNameMatcher.Normalize(candidate.Title))
            .Distinct(StringComparer.Ordinal).Count() == 1 ? selection.Candidates[0].Title : null;
}
