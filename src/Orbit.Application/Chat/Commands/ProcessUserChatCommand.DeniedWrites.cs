using System.Text.Json;
using Orbit.Application.Common;
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
            if (!outcome.ToolResult.Success)
                continue;
            if (original.Outcome.ActionResult is { } action)
                results.ActionResults.Remove(action);
            if (original.Outcome.OperationResult is { } operation)
                results.OperationResults.Remove(operation);
            if (original.Outcome.PolicyDenial is { } denial)
                results.PolicyDenials.Remove(denial);
            results.ToolResults.Remove(original.Outcome.ToolResult);
        }
        return outcomes;
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
        var unknown = new List<string>();
        CollectUnknownTargets(call.Args, owned, unknown);
        var error = unknown.Count > 0
            ? $"These target IDs are not IDs of this person's records: {string.Join(", ", unknown.Distinct(StringComparer.Ordinal))}. Copy each ID exactly, with all 36 characters, from the query_habits result or the validated choice selection of this turn."
            : "A target ID is not an ID of this person's records. Copy each ID exactly, with all 36 characters, from a read result or the validated choice selection of this turn.";
        return outcome with { ToolResult = outcome.ToolResult with { Error = error } };
    }

    private static void CollectUnknownTargets(JsonElement node, HashSet<Guid> ownedHabits, List<string> unknown)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) CollectUnknownTargets(item, ownedHabits, unknown);
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
                {
                    var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
                    if (!Guid.TryParse(text, out var id) || habit && !ownedHabits.Contains(id))
                        unknown.Add(text);
                }
            }
            else if (property.Name is "positions" or "habits" or "sub_habits" or "filter")
                CollectUnknownTargets(property.Value, ownedHabits, unknown);
        }
    }

    /// <summary>
    /// A write of the turn that did not prepare beside a write that did leaves a preview with only a part of the
    /// request. The turn then holds no preview, and the answer says that the request was not prepared.
    /// </summary>
    private bool LeavesPartOfTheRequest(ToolExecutionAccumulator results) =>
        results.PendingOperations.Count > 0
        && results.OperationResults.Any(operation => operation.Status is AgentOperationStatus.Failed
                or AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy
            && operation.ErrorCode != ErrorCodes.HabitLimitReached
            && (operation.Access == "write" || ai.ToolRegistry.GetTool(operation.OperationId) is { IsReadOnly: false }));

    private static void WithdrawPartialPreview(ToolExecutionAccumulator results, string? language)
    {
        foreach (var pending in results.PendingOperations.ToArray())
            results.SupersedePendingOperation(pending.Id);
        results.SanitizeFailedActions(RequestNotPreparedMessage(language));
        results.RequestNotPrepared = true;
    }

    private static string RequestNotPreparedMessage(string? language) =>
        ErrorCopy.Resolve("chat.answer.requestNotPrepared", LocaleHelper.IsPortuguese(language));
}
