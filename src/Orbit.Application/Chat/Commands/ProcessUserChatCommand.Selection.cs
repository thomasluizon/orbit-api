using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool HasDeclaredReadPurpose(AiToolCall call, ProcessUserChatCommand request) =>
        call.Name != "query_habits" || request.ClientContext?.SupportsParts != true
        || request.ClientContext.PartKinds?.Any(IsAskKind) != true
        || call.Args.TryGetProperty("for_selection", out var selection) && selection.ValueKind == JsonValueKind.False;

    /// <summary>
    /// A read whose message names every match by its distinct complete title selects nothing. A read with no
    /// declared purpose needs no answer when the message names the whole set or members by order of a same-titled
    /// group, and the guard checks its candidates again for each write. A for_selection=true read still asks.
    /// </summary>
    private static AgentOperationResult ResolveNamedSelection(AiToolCall call, AgentOperationResult operation, string message)
    {
        if (call.Name != "query_habits" || IsSelectionRead(call)
            || operation.Payload is not HabitListCard { Selection: { } selection } card)
            return operation;
        if (card.TotalCount == selection.Candidates.Count && NamesEveryCandidate(message, selection.Candidates))
            return operation with { Payload = card with { Selection = null } };
        return selection.RequiresAnswer && MessageScopesAGroup(message, selection)
            ? operation with { Payload = card with { Selection = selection with { RequiresAnswer = false } } }
            : operation;
    }

    private static bool MessageScopesAGroup(string message, HabitSelectionCandidates selection) =>
        selection.Candidates.GroupBy(candidate => HabitNameMatcher.Normalize(candidate.Title))
            .Any(group => group.Count() > 1 && MessageScopesGroup(message, group.Key));

    /// <summary>
    /// The message names the whole set or members by order of one same-titled group. The guard and the read result
    /// both read this one decision.
    /// </summary>
    private static bool MessageScopesGroup(string message, string title) =>
        HabitSetRequestScope.NamesMembersByOrder(message, title) || HabitSetRequestScope.NamesWholeSet(message, title);

    private static bool NamesEveryCandidate(string message, IReadOnlyList<HabitListCardItem> candidates)
    {
        var text = $" {HabitNameMatcher.Normalize(message)} ";
        var titles = candidates.Select(candidate => $" {HabitNameMatcher.Normalize(candidate.Title)} ").ToArray();
        return titles.Length > 1 && titles.Distinct(StringComparer.Ordinal).Count() == titles.Length
            && titles.All(title => title.Trim().Length > 0 && text.Contains(title, StringComparison.Ordinal)
                && titles.All(other => other == title || !other.Contains(title, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The selection guard's rule. A write that the guard holds and a read whose candidates need an answer both
    /// return this text, so the model reads one rule for when a write over the candidates is accepted.
    /// </summary>
    private const string SelectionRule = "Selection is unanswered. Call ask with the returned candidates and prepare no write until the person answers. A request that names the whole set or members by order of several candidates with the same title needs no question; a word in a time phrase, a filler phrase or the title, such as every day, todos os dias or one last thing, names neither. Writes in one round that make different changes and together cover every candidate with that title exactly once are accepted. A candidate with a title of its own always waits for the answer.";

    private static ToolCallOutcome SelectionRequiredOutcome(AiToolCall call) => new(
        new AiToolCallResult(call.Name, call.Id, false, null, null, SelectionRule),
        null, null, null, null, FailureKey: "selection_unanswered");

    /// <summary>
    /// Holds each write whose selection is unanswered. A held write makes no change, so it covers no part of a split:
    /// the check runs again without the held writes until no further write is held. Returns the scope of the writes
    /// that remain.
    /// </summary>
    private static SelectionScope RejectUnansweredSelections(IReadOnlyList<AiToolCall> calls,
        Dictionary<string, AiToolCall?> resolvedCalls, ToolExecutionAccumulator results,
        Dictionary<string, ToolCallOutcome> outcomes, SelectionScope scope)
    {
        var held = new List<HabitSelectionCandidates>();
        while (true)
        {
            var settled = new List<SettledSelection>();
            var heldWrites = new List<AiToolCall>();
            foreach (var call in calls)
            {
                if (resolvedCalls[call.Id] is not { } resolved)
                    continue;
                var verdict = SelectionForWrite(resolved, results, scope);
                settled.AddRange(verdict.Settled);
                if (verdict.Unanswered is not { } unanswered)
                    continue;
                held.Add(unanswered);
                heldWrites.Add(resolved);
                results.PendingSelection = unanswered;
                outcomes[call.Id] = SelectionRequiredOutcome(call);
                resolvedCalls[call.Id] = null;
            }
            if (heldWrites.Count == 0)
            {
                SettleSelections(results, settled.Where(item => !held.Contains(item.Selection, ReferenceEqualityComparer.Instance)));
                return scope;
            }
            scope = scope with
            {
                TurnWrites = [.. scope.TurnWrites.Where(write => !heldWrites.Contains(write, ReferenceEqualityComparer.Instance))]
            };
        }
    }

    /// <summary>
    /// Takes the candidates that the checks covered out of each settled selection. The other candidates stay as a
    /// selection that needs no answer, so a later write to one of them is checked again.
    /// </summary>
    private static void SettleSelections(ToolExecutionAccumulator results, IEnumerable<SettledSelection> settled)
    {
        foreach (var group in settled.GroupBy<SettledSelection, HabitSelectionCandidates>(item => item.Selection,
            ReferenceEqualityComparer.Instance))
        {
            var covered = group.SelectMany(item => item.CoveredIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rest = group.Key.Candidates.Where(candidate => !covered.Contains(candidate.Id)).ToArray();
            var index = results.SelectionCandidates.FindIndex(selection => ReferenceEquals(selection, group.Key));
            if (index >= 0 && rest.Length == 0)
                results.SelectionCandidates.RemoveAt(index);
            else if (index >= 0)
                results.SelectionCandidates[index] = group.Key with { Candidates = rest, RequiresAnswer = false };
            if (ReferenceEquals(results.PendingSelection, group.Key))
                results.PendingSelection = results.SelectionCandidates.FirstOrDefault(selection => selection.RequiresAnswer);
        }
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
                null, null, null, null, FailureKey: "unread_duplicate_targets");
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
    /// run, when the person answered it, or, for a group of two or more same-titled records, when the message names
    /// the whole set or members by order, or when the turn's writes make different changes that split the group
    /// between them without a gap or an overlap.
    /// </summary>
    private sealed record SelectionScope(string Message, IReadOnlyList<AiToolCall> TurnWrites);

    private sealed record SelectionVerdict(HabitSelectionCandidates? Unanswered, IReadOnlyList<SettledSelection> Settled);

    /// <summary>
    /// A selection that a write settled, with the candidates that the checks covered: the candidates that the write
    /// targets and every member of the title groups that the write touches.
    /// </summary>
    private sealed record SettledSelection(HabitSelectionCandidates Selection, IReadOnlyCollection<string> CoveredIds);

    private static SelectionScope SelectionScopeOf(ProcessUserChatCommand request,
        Dictionary<string, AiToolCall?> resolvedCalls, ToolExecutionAccumulator results) =>
        new(request.Message, [.. resolvedCalls.Values.OfType<AiToolCall>(), .. results.PreviousWriteCalls]);

    private static SelectionVerdict SelectionForWrite(AiToolCall call, ToolExecutionAccumulator results, SelectionScope scope)
    {
        var targets = EffectiveWriteTargetIds(call, results).ToHashSet();
        var ids = targets.Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unanswered = targets.Where(id => !results.AnsweredTargetIds.Contains(id))
            .Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settled = new List<SettledSelection>();
        var selections = new[] { results.PendingSelection }.Concat(results.SelectionCandidates).OfType<HabitSelectionCandidates>()
            .Distinct(ReferenceEqualityComparer.Instance).Cast<HabitSelectionCandidates>();
        foreach (var selection in selections)
        {
            if (!selection.Candidates.Any(candidate => ids.Contains(candidate.Id)))
                continue;
            var touched = selection.Candidates.Where(candidate => unanswered.Contains(candidate.Id))
                .GroupBy(candidate => HabitNameMatcher.Normalize(candidate.Title)).Select(group => group.Key).ToHashSet();
            var groups = selection.Candidates.GroupBy(candidate => HabitNameMatcher.Normalize(candidate.Title))
                .Where(group => touched.Contains(group.Key));
            if (!groups.All(group => GroupIsSettled(group.Key, group.Select(candidate => candidate.Id).ToArray(), scope, results)))
                return new SelectionVerdict(selection, settled);
            settled.Add(new SettledSelection(selection, [.. selection.Candidates
                .Where(candidate => ids.Contains(candidate.Id) || touched.Contains(HabitNameMatcher.Normalize(candidate.Title)))
                .Select(candidate => candidate.Id)]));
        }
        return new SelectionVerdict(null, settled);
    }

    /// <summary>
    /// A group with one record has no same-titled set for a set word, an order word or a split to scope. A write to
    /// that record keeps the base rule: it waits for the answer.
    /// </summary>
    private static bool GroupIsSettled(string title, IReadOnlyList<string> members, SelectionScope scope,
        ToolExecutionAccumulator results)
    {
        if (members.Count < 2)
            return false;
        if (HabitSetRequestScope.NamesMembersByOrder(scope.Message, title))
            return true;
        var writes = scope.TurnWrites
            .Select(write => (Call: write, Targets: EffectiveWriteTargetIds(write, results).Select(id => id.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .Where(write => members.Any(write.Targets.Contains)).ToArray();
        var hits = members.Select(member => writes.Count(write => write.Targets.Contains(member))).ToArray();
        if (HabitSetRequestScope.NamesWholeSet(scope.Message, title) && hits.All(count => count >= 1))
            return true;
        return writes.Length >= 2 && hits.All(count => count == 1) && !MakeOneChange([.. writes.Select(write => write.Call)]);
    }

    /// <summary>
    /// Writes that make the same change, each to a part of a group, make one change to the whole group. That is a
    /// whole-set write and not a split, so only a message that names the whole set settles it.
    /// </summary>
    private static bool MakeOneChange(IReadOnlyList<AiToolCall> writes) =>
        writes.All(write => MakeSameChange(write, writes[0]));

    /// <summary>
    /// Two writes make the same change when one tool sends the same arguments apart from the record ids, or when they
    /// are the single and the bulk form of one operation, such as delete_habit and bulk_delete_habits. The two forms
    /// carry the change in different arguments, so they compare by the operation alone.
    /// </summary>
    private static bool MakeSameChange(AiToolCall write, AiToolCall other) => write.Name == other.Name
        ? JsonNode.DeepEquals(ChangeOf(write.Args), ChangeOf(other.Args))
        : BulkToolRepeatGuard.IsBulkAlternative(write.Name, other.Name)
            || BulkToolRepeatGuard.IsBulkAlternative(other.Name, write.Name);

    /// <summary>
    /// The arguments of a write without the record ids, so that two writes compare by their change alone.
    /// </summary>
    private static JsonNode? ChangeOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(element.EnumerateObject().Where(property => !IsRecordId(property.Value))
            .Select(property => KeyValuePair.Create(property.Name, ChangeOf(property.Value)))),
        JsonValueKind.Array => new JsonArray([.. element.EnumerateArray().Where(item => !IsRecordId(item)).Select(ChangeOf)]),
        _ => JsonValue.Create(element)
    };

    private static bool IsRecordId(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && element.TryGetGuid(out _);

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
        var reference = string.IsNullOrWhiteSpace(selection.Reference) ? null : SharedTitle(selection) ?? selection.Reference;
        var text = string.IsNullOrWhiteSpace(reference)
            ? ErrorCopy.Resolve("chat.answer.selectHabit", LocaleHelper.IsPortuguese(language))
            : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                ErrorCopy.Resolve("chat.answer.selectHabitReference", LocaleHelper.IsPortuguese(language)), reference);
        return new ResponseCards(text, Parts: parts ? [new ChatTextPart(text)] : null) { ServerComposedText = true };
    }

    /// <summary>
    /// The stored title that every candidate shares. A question about a search quotes it instead of the raw search
    /// text, so a title with punctuation reads as the person sees it in the list.
    /// </summary>
    private static string? SharedTitle(HabitSelectionCandidates selection) =>
        selection.Candidates.Count > 0 && selection.Candidates.Select(candidate => HabitNameMatcher.Normalize(candidate.Title))
            .Distinct(StringComparer.Ordinal).Count() == 1 ? selection.Candidates[0].Title : null;
}
