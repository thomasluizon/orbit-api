using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static readonly string[] HabitTargetFields = ["habit_id", "habit_ids", "parent_habit_id", "new_parent_id", "parent_id"];
    private static readonly string[] OtherTargetFields = ["tag_id", "tag_ids", "goal_id", "goal_ids", "notification_id",
        "notification_ids", "template_id", "key_id", "suggestion_id"];

    /// <summary>
    /// The correction of the writes that the ownership check turned down beside a prepared write. The model sends each
    /// denied write once more on the right ids, and a resend that prepares replaces its denial.
    /// </summary>
    private sealed class DeniedWriteCorrection(IReadOnlyList<CorrectionOccurrence> occurrences) : ICallCorrection
    {
        private readonly HashSet<CorrectionOccurrence> _unresolved = occurrences.Where(item => IsOwnershipWriteDenial(item.Outcome)).ToHashSet();

        public bool HasUnresolvedFailures => _unresolved.Count > 0;

        /// <summary>
        /// Every write of the turn that did not prepare was turned down by the ownership check, and every other write
        /// prepared. A failed write or a read denial beside them leaves nothing that this correction replaces.
        /// </summary>
        public static bool CanReplace(IReadOnlyList<CorrectionOccurrence> occurrences) =>
            occurrences.Any(item => IsOwnershipWriteDenial(item.Outcome))
            && occurrences.All(item => IsOwnershipWriteDenial(item.Outcome) || item.Outcome.ToolResult.Success);

        /// <summary>
        /// Each candidate sends one denied write again: the same operation in its single or bulk form, with the same
        /// change and the records that the denied write named correctly. A candidate for another change or another
        /// delete binds to no denial, so the correction fails. Each candidate runs through every write guard of the
        /// round again, so a corrected id must still be one the person owns.
        /// </summary>
        public IReadOnlyList<(AiToolCall Candidate, CorrectionOccurrence Original)> Bind(IReadOnlyList<AiToolCall> candidates,
            AiToolRegistry registry, ToolExecutionAccumulator results)
        {
            var replacements = new List<(AiToolCall, CorrectionOccurrence)>();
            foreach (var candidate in candidates)
            {
                var original = _unresolved.FirstOrDefault(item => ResendsDeniedWrite(item.Call, candidate, results)
                    && replacements.All(replacement => replacement.Item2 != item));
                if (registry.GetTool(candidate.Name)?.IsReadOnly != false || original is null)
                    return [];
                replacements.Add((candidate, original));
            }
            return replacements;
        }

        public void Resolve(CorrectionOccurrence original) => _unresolved.Remove(original);
    }

    /// <summary>
    /// Runs each write that replaces a denied write through the same write guards as the original round, so the
    /// selection check, the unread-target check and the answer checkpoint apply to the corrected ids too.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ToolCallOutcome>> ExecuteDeniedWriteCorrectionsAsync(
        IReadOnlyList<AiToolCall> candidates, DeniedWriteCorrection correction,
        ProcessUserChatCommand request, ToolExecutionAccumulator results, string? language, CancellationToken ct)
    {
        var bound = correction.Bind(candidates, ai.ToolRegistry, results);
        if (bound.Count == 0)
            return new Dictionary<string, ToolCallOutcome>(StringComparer.Ordinal);
        var outcomes = await ExecuteToolCallsAsync([.. bound.Select(item => item.Candidate)], request, results, language, ct);
        foreach (var (candidate, original) in bound)
        {
            if (!outcomes.TryGetValue(candidate.Id, out var outcome) || !PreparesWrite(outcome))
                continue;
            correction.Resolve(original);
            DropDeniedWrite(original.Outcome, results);
        }
        return outcomes;
    }

    /// <summary>
    /// After a denial with no prepared write beside it, the model can read and send the write again without a
    /// correction. A later write that prepares and sends the denied write again replaces the denial, so the end of the
    /// loop does not count it as a missing part of the request, and the person sees no failure for it. A write for
    /// another change or another record leaves the denial in place.
    /// </summary>
    private static void ReplaceResentDeniedWrites(IReadOnlyList<AiToolCall> calls,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes, ToolExecutionAccumulator results)
    {
        foreach (var call in calls)
        {
            if (!outcomes.TryGetValue(call.Id, out var outcome) || !PreparesWrite(outcome)
                || results.CorrectionOccurrences.FirstOrDefault(item => IsOwnershipWriteDenial(item.Outcome)
                    && ResendsDeniedWrite(item.Call, call, results)) is not { } denied)
                continue;
            results.CorrectionOccurrences.Remove(denied);
            DropDeniedWrite(denied.Outcome, results);
        }
    }

    /// <summary>The write prepared a preview or ran. A call that repeats a prepared write prepares nothing.</summary>
    private static bool PreparesWrite(ToolCallOutcome outcome) => outcome.ToolResult.Success
        && (outcome.PendingOperation is not null || outcome.OperationResult?.Status == AgentOperationStatus.Succeeded);

    /// <summary>
    /// The later write is the denied write sent again: the same operation and change (<see cref="MakeSameChange"/>
    /// without the targets) and evidence for each record. It names the chosen or the unchosen records of the validated
    /// choice selection, or each record of the denied write and at least as many targets. An unknown denied id names
    /// only the habit that it was copied from (<see cref="CopiedFrom"/>), so no other write replaces its denial.
    /// </summary>
    private static bool ResendsDeniedWrite(AiToolCall denied, AiToolCall candidate, ToolExecutionAccumulator results)
    {
        var change = denied with { Args = ChangeArguments(denied.Args) };
        if (change.Args.ValueKind != JsonValueKind.Object
            || !MakeSameChange(change, candidate with { Args = ChangeArguments(candidate.Args) }))
            return false;
        var resentTargets = TargetsOf(candidate.Args);
        var resentIds = resentTargets.Select(target => Guid.TryParse(target.Value, out var id) ? id : Guid.Empty).ToHashSet();
        if (results.Recording?.ChoiceSelection is { } choice && resentTargets.All(target => target.Habit)
            && new[] { choice.ChosenTargets, choice.UnchosenTargets }.Any(set => set.Count > 0
                && resentIds.SetEquals(set.Select(target => Guid.TryParse(target, out var id) ? id : Guid.Empty))))
            return true;
        var owned = results.AllHabits.Select(habit => habit.Id).ToArray();
        var resentValues = resentTargets.Select(target => target.Value).ToHashSet(StringComparer.Ordinal);
        var deniedTargets = TargetsOf(denied.Args);
        return resentValues.Count >= deniedTargets.Select(target => target.Value).Distinct(StringComparer.Ordinal).Count()
            && deniedTargets.All(target => !target.Habit ? resentValues.Contains(target.Value)
                : Guid.TryParse(target.Value, out var id) && owned.Contains(id) ? resentIds.Contains(id)
                : CopiedFrom(target.Value, owned) is { } copied && resentIds.Contains(copied));
    }

    /// <summary>
    /// The one owned habit that a denied id was copied from: the id cut short at the end of its groups, with the first
    /// group whole, or one character off. Null when no habit or more than one habit fits.
    /// </summary>
    private static Guid? CopiedFrom(string value, IReadOnlyCollection<Guid> owned)
    {
        var copy = value.Trim().ToLowerInvariant();
        var sources = owned.Where(id => IsCutShort(copy, id.ToString()) || IsOneCharacterOff(copy, id.ToString())).Take(2).ToArray();
        return sources.Length == 1 ? sources[0] : null;
    }

    private static bool IsCutShort(string copy, string id)
    {
        var groups = copy.Split('-');
        var source = id.Split('-');
        return copy.Length < id.Length && groups.Length <= source.Length && groups[0] == source[0]
            && groups.Zip(source).All(pair => pair.First.Length > 0 && pair.Second.StartsWith(pair.First, StringComparison.Ordinal));
    }

    private static bool IsOneCharacterOff(string copy, string id)
    {
        if (copy == id || Math.Abs(copy.Length - id.Length) > 1)
            return false;
        var (shorter, longer) = copy.Length <= id.Length ? (copy, id) : (id, copy);
        var index = 0;
        while (index < shorter.Length && shorter[index] == longer[index])
            index++;
        return shorter.Length == longer.Length
            ? shorter[(index + 1)..] == longer[(index + 1)..]
            : shorter[index..] == longer[(index + 1)..];
    }

    /// <summary>
    /// The arguments of a model call without its target fields, its <c>orbit_</c> directives and its null fields. A
    /// target goes by its field name, so a malformed id leaves the change as a valid id does.
    /// </summary>
    private static JsonElement ChangeArguments(JsonElement args) => JsonSerializer.SerializeToElement(WithoutTargets(args));

    private static JsonNode? WithoutTargets(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(node.EnumerateObject()
            .Where(property => property.Value.ValueKind != JsonValueKind.Null
                && !property.Name.StartsWith("orbit_", StringComparison.Ordinal)
                && !HabitTargetFields.Contains(property.Name, StringComparer.Ordinal)
                && !OtherTargetFields.Contains(property.Name, StringComparer.Ordinal))
            .Select(property => KeyValuePair.Create(property.Name, WithoutTargets(property.Value)))),
        JsonValueKind.Array => new JsonArray([.. node.EnumerateArray().Select(WithoutTargets)]),
        _ => JsonValue.Create(node)
    };

    private static void DropDeniedWrite(ToolCallOutcome denied, ToolExecutionAccumulator results)
    {
        if (denied.ActionResult is { } action)
            results.ActionResults.Remove(action);
        if (denied.OperationResult is { } operation)
            results.OperationResults.Remove(operation);
        if (denied.PolicyDenial is { } denial)
            results.PolicyDenials.Remove(denial);
        results.ToolResults.Remove(denied.ToolResult);
    }

    /// <summary>
    /// The ownership check reports a malformed id and a foreign id with one reason. The model gets the ids that are
    /// not ids of the person's records and where the right ids are. The text is the same for an id that does not
    /// exist and for an id of another account, so it tells nothing about another account.
    /// </summary>
    private static ToolCallOutcome DescribeUnownedTargets(AiToolCall call, ToolCallOutcome outcome, ToolExecutionAccumulator results)
    {
        if (!IsOwnershipWriteDenial(outcome))
            return outcome;
        var owned = results.AllHabits.Select(habit => habit.Id).ToHashSet();
        var unknown = TargetsOf(call.Args).Where(target => !Guid.TryParse(target.Value, out var id) || target.Habit && !owned.Contains(id))
            .Select(target => target.Value).ToList();
        var error = unknown.Count > 0
            ? $"These target IDs are not IDs of this person's records: {string.Join(", ", unknown.Distinct(StringComparer.Ordinal))}. Copy each ID exactly, with all 36 characters, from the Active Habit Index, a query_habits result or the validated choice selection of this turn."
            : "A target ID is not an ID of this person's records. Copy each ID exactly, with all 36 characters, from the Active Habit Index, a read result or the validated choice selection of this turn.";
        return outcome with { ToolResult = outcome.ToolResult with { Error = error } };
    }

    /// <summary>The target ids that the ownership check reads, each marked when it names a habit.</summary>
    private static List<(string Value, bool Habit)> TargetsOf(JsonElement args)
    {
        var targets = new List<(string Value, bool Habit)>();
        Collect(args);
        return targets;

        void Collect(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Collect(item);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object)
                return;
            foreach (var property in node.EnumerateObject())
            {
                var habit = HabitTargetFields.Contains(property.Name, StringComparer.Ordinal);
                if (habit || OtherTargetFields.Contains(property.Name, StringComparer.Ordinal))
                {
                    var values = property.Value.ValueKind == JsonValueKind.Array ? property.Value.EnumerateArray().ToArray() : [property.Value];
                    foreach (var value in values.Where(value => value.ValueKind != JsonValueKind.Null))
                        targets.Add((value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText(), habit));
                }
                else if (property.Name is "positions" or "habits" or "sub_habits" or "filter")
                    Collect(property.Value);
            }
        }
    }
}
