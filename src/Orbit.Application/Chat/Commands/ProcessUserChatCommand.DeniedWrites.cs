using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static readonly string[] HabitTargetFields = ["habit_id", "habit_ids", "parent_habit_id", "new_parent_id", "parent_id"];
    private static readonly string[] OtherTargetFields = ["tag_id", "tag_ids", "goal_id", "goal_ids", "notification_id",
        "notification_ids", "template_id", "key_id", "suggestion_id"];

    /// <summary>
    /// Runs each write that replaces a denied write through the same write guards as the original round, so the
    /// selection check, the unread-target check and the answer checkpoint apply to the corrected ids too.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ToolCallOutcome>> ExecuteDeniedWriteCorrectionsAsync(
        IReadOnlyList<(AiToolCall Candidate, CorrectionOccurrence Original)> bound, RejectedCallCorrection correction,
        ProcessUserChatCommand request, ToolExecutionAccumulator results, string? language, CancellationToken ct)
    {
        if (bound.Count == 0)
            return new Dictionary<string, ToolCallOutcome>(StringComparer.Ordinal);
        var outcomes = await ExecuteToolCallsAsync([.. bound.Select(item => item.Candidate)], request, results, language, ct);
        foreach (var (candidate, original) in bound)
        {
            if (!outcomes.TryGetValue(candidate.Id, out var outcome))
                continue;
            correction.Accept(original, outcome);
            if (PreparesWrite(outcome))
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
    /// without the targets) and a link to the records. It names exactly the chosen or the unchosen records of the
    /// validated choice selection, or each habit that the denied write named correctly and at least as many targets.
    /// Without the selection, a delete or another write that sets no field beside its targets shows no such link.
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
        if (!change.Args.EnumerateObject().Any(property => property.Name != "filter"))
            return false;
        var owned = results.AllHabits.Select(habit => habit.Id).ToHashSet();
        var deniedTargets = TargetsOf(denied.Args);
        return resentTargets.Select(target => target.Value).Distinct(StringComparer.Ordinal).Count()
                >= deniedTargets.Select(target => target.Value).Distinct(StringComparer.Ordinal).Count()
            && deniedTargets.All(target => !target.Habit || !Guid.TryParse(target.Value, out var id) || !owned.Contains(id)
                || resentIds.Contains(id));
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

    /// <summary>
    /// A write that failed, or that the ownership check turned down, beside a prepared write leaves a part of the
    /// request that a second send can prepare, so the turn holds no preview and says so. A plan, feature or policy
    /// denial does not change on a second send: the turn keeps the prepared part and the reason for the denial.
    /// </summary>
    private bool LeavesPartOfTheRequest(ToolExecutionAccumulator results) =>
        results.PreparedPendingOperations.Any()
        && results.OperationResults.Any(operation => IsOwnershipWriteDenial(operation)
            || operation.Status == AgentOperationStatus.Failed && operation.ErrorCode != ErrorCodes.HabitLimitReached
            && (operation.Access == "write" || ai.ToolRegistry.GetTool(operation.OperationId) is { IsReadOnly: false }));

    /// <summary>
    /// Withdraws only what this turn prepared, so the turn changes nothing that the person had before it. A preview
    /// that a write of this turn created is closed in the pending operation store too, so no approval, no replay of the
    /// answer checkpoint and no later turn brings it back. An earlier preview that this turn revised gets its earlier
    /// state back and stays approvable. An earlier preview that this turn only read stays as it is.
    /// </summary>
    private void WithdrawPartialPreview(Guid userId, ToolExecutionAccumulator results, string? language)
    {
        results.KeepsEarlierPreview = results.PendingOperations.Any(pending => results.EarlierPendingIds.Contains(pending.Id));
        foreach (var pending in results.PreparedPendingOperations.ToArray())
        {
            if (execution.PendingOperationStore.GetExecution(userId, pending.Id) is { } stored)
            {
                var fingerprint = AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText());
                if (!results.RevisedEarlierPreviews.TryGetValue(pending.Id, out var earlier))
                    execution.PendingOperationStore.Cancel(userId, pending.Id, fingerprint);
                else if (stored.PreviewFingerprint == pending.PreviewFingerprint)
                    execution.PendingOperationStore.Restore(userId, pending.Id, fingerprint, earlier);
            }
            results.SupersedePendingOperation(pending.Id);
        }
        results.SanitizeFailedActions(RequestNotPreparedMessage(results, language));
        results.RequestNotPrepared = true;
    }

    /// <summary>
    /// The answer of a withdrawn turn. It says that nothing waits for approval only when no earlier preview that the
    /// turn read or revised still waits.
    /// </summary>
    private static string RequestNotPreparedMessage(ToolExecutionAccumulator results, string? language) =>
        ErrorCopy.Resolve(results.KeepsEarlierPreview ? "chat.answer.requestNotPreparedEarlierKept" : "chat.answer.requestNotPrepared",
            LocaleHelper.IsPortuguese(language));

    /// <summary>
    /// Keeps the stored state of each earlier preview that a revision in this round changes, before the revision runs,
    /// so a turn that ends without its whole request can put the preview back.
    /// </summary>
    private Dictionary<string, (Guid Id, PendingAgentOperationRevision Revision)> ReadEarlierRevisions(
        IEnumerable<AiToolCall> calls, Guid userId, ToolExecutionAccumulator results)
    {
        var revisions = new Dictionary<string, (Guid, PendingAgentOperationRevision)>(StringComparer.Ordinal);
        foreach (var call in calls.Where(call => call.Name == "revise_pending_changes" && !RevisePendingChangesTool.IsInspection(call.Args)))
            if (Guid.TryParse(JsonArgumentParser.GetOptionalString(call.Args, "pending_operation_id"), out var id)
                && results.EarlierPendingIds.Contains(id) && !results.RevisedEarlierPreviews.ContainsKey(id)
                && execution.PendingOperationStore.GetRevision(userId, id) is { } revision)
                revisions[call.Id] = (id, revision);
        return revisions;
    }

    private static void KeepEarlierRevisions(IReadOnlyDictionary<string, (Guid Id, PendingAgentOperationRevision Revision)> revisions,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes, ToolExecutionAccumulator results)
    {
        foreach (var (callId, (id, revision)) in revisions)
            if (outcomes.TryGetValue(callId, out var outcome) && outcome.PendingOperation is not null)
                results.RevisedEarlierPreviews.TryAdd(id, revision);
    }
}
