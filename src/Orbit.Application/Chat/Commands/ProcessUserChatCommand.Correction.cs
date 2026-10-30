using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    internal sealed record CorrectionOccurrence(AiToolCall Call, ToolCallOutcome Outcome);

    private static bool IsOwnershipReadDenial(ToolCallOutcome outcome) =>
        outcome.OperationResult is { Status: AgentOperationStatus.Denied, Access: "read", PolicyReason: { } reason }
        && reason.StartsWith("target_not_owned:", StringComparison.Ordinal);

    private static void OfferCorrections(List<AiToolCallResult> results, IReadOnlyDictionary<string, ToolCallOutcome> outcomes)
    {
        for (var index = 0; index < results.Count; index++)
            if (outcomes.TryGetValue(results[index].Id, out var outcome))
            {
                if (IsOwnershipReadDenial(outcome))
                    results[index] = results[index] with
                    {
                        Payload = new
                        {
                            correction_allowed = true,
                            instruction = "Correct only this read once using an owned ID from the appropriate account index, or omit it if successful reads already answer the request. Do not add writes or repeat successful reads."
                        }
                    };
                else if (outcome.PreparedResponse is RejectedBeforeDispatchResponse { Operation.Status: AgentOperationStatus.Failed })
                    results[index] = results[index] with { Payload = new { correction_allowed = true } };
            }
    }

    /// <summary>
    /// An isolated starting-step suggestion can consume one correction without account changes.
    /// A write must be rejected before dispatch and certify the same complete prepared intent
    /// across all original occurrences, including successful siblings. Dispatch uses the original preparation;
    /// a candidate cannot select its parent, change intent, replay an effect or claim an outcome.
    /// </summary>
    private sealed class RejectedCallCorrection(IReadOnlyList<CorrectionOccurrence> occurrences)
    {
        private readonly HashSet<CorrectionOccurrence> _unresolved = occurrences
            .Where(item => item.Outcome.OperationResult?.Status == AgentOperationStatus.Failed || IsOwnershipReadDenial(item.Outcome)).ToHashSet();
        private bool _consumed;

        public bool HasUnresolvedFailures => _unresolved.Count > 0;
        public bool IsReadOnly => occurrences.Count > 0 && occurrences.All(item => IsOwnershipReadDenial(item.Outcome));

        public static bool CanRecover(IReadOnlyList<CorrectionOccurrence> occurrences) =>
            occurrences.Any(item => IsOwnershipReadDenial(item.Outcome))
            ? occurrences.All(item => IsOwnershipReadDenial(item.Outcome))
            : occurrences.Count == 1 && IsFailedStartingStep(occurrences[0])
            || occurrences.Any(item => item.Outcome.OperationResult?.Status == AgentOperationStatus.Failed)
            && occurrences.Where(item => item.Outcome.OperationResult?.Status == AgentOperationStatus.Failed)
                .All(item => item.Outcome.PreparedResponse is RejectedBeforeDispatchResponse);

        public async Task<IReadOnlyList<(AiToolCall Candidate, CorrectionOccurrence Original)>> BindAsync(
            IReadOnlyList<AiToolCall> candidates, AiToolRegistry registry, CancellationToken ct)
        {
            if (_consumed)
                return [];
            _consumed = true;
            try
            {
                return await PrepareBindingsAsync(candidates, registry, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return [];
            }
        }

        private async Task<IReadOnlyList<(AiToolCall Candidate, CorrectionOccurrence Original)>> PrepareBindingsAsync(
            IReadOnlyList<AiToolCall> candidates, AiToolRegistry registry, CancellationToken ct)
        {
            if (IsReadOnly)
            {
                var reads = new List<(AiToolCall, CorrectionOccurrence)>();
                foreach (var candidate in candidates)
                {
                    var original = _unresolved.FirstOrDefault(item => item.Call.Name == candidate.Name
                        && reads.All(bound => bound.Item2 != item));
                    if (registry.GetTool(candidate.Name)?.IsReadOnly != true || original is null)
                        return [];
                    reads.Add((candidate, original));
                }
                return reads;
            }
            if (occurrences.Count == 1 && IsFailedStartingStep(occurrences[0]))
                return candidates.Count == 1 && IsStartingStepCall(candidates[0])
                    ? [(candidates[0], occurrences[0])] : [];
            var bound = new List<(AiToolCall, CorrectionOccurrence)>();
            var selected = new HashSet<CorrectionOccurrence>();
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (candidate.Args.ValueKind != System.Text.Json.JsonValueKind.Object
                    || registry.GetTool(candidate.Name) is not IPreparedIntentTool tool)
                    return [];
                var sameTool = occurrences.Where(item => item.Call.Name == candidate.Name).ToArray();
                if (sameTool.Length == 0 || sameTool.Any(item => item.Outcome.PreparedResponse is null))
                    return [];
                var prepared = await tool.PrepareIntentAsync(candidate.Args, sameTool[0].Outcome.PreparedResponse!.Request.UserId, ct);
                if (prepared is null || prepared.ArgumentValidation.IsFailure)
                    return [];
                var matches = sameTool.Where(item => item.Outcome.PreparedResponse!.Intent.HasSameMeaning(prepared)).ToArray();
                if (matches.Length != 1 || !_unresolved.Contains(matches[0]) || !selected.Add(matches[0])
                    || matches[0].Outcome.PreparedResponse is not RejectedBeforeDispatchResponse rejected
                    || rejected.Operation.Status != AgentOperationStatus.Failed
                    || rejected.Request.OperationId != candidate.Name)
                    return [];
                bound.Add((candidate, matches[0]));
            }
            return bound;
        }

        public void Accept(CorrectionOccurrence original, ToolCallOutcome outcome)
        {
            if (IsReadOnly && outcome.OperationResult?.Status == AgentOperationStatus.Succeeded
                || IsFailedStartingStep(original)
                    && outcome.OperationResult is { Status: AgentOperationStatus.Succeeded, Payload: StartingStep }
                || outcome.PreparedResponse?.Operation.Status is AgentOperationStatus.Succeeded or AgentOperationStatus.PendingConfirmation)
                _unresolved.Remove(original);
        }

        public bool CanOmitReads(ToolExecutionAccumulator results)
        {
            var remaining = results.OperationResults.Where(operation => _unresolved.All(item => item.Outcome.OperationResult != operation)).ToArray();
            return IsReadOnly && remaining.Length > 0
                && remaining.All(operation => operation is { Status: AgentOperationStatus.Succeeded, ReadBlock: not null });
        }

        public void OmitReads(ToolExecutionAccumulator results)
        {
            foreach (var original in _unresolved)
            {
                if (original.Outcome.OperationResult is { } operation) results.OperationResults.Remove(operation);
                if (original.Outcome.PolicyDenial is { } denial) results.PolicyDenials.Remove(denial);
                results.ToolResults.Remove(original.Outcome.ToolResult);
            }
            _unresolved.Clear();
        }
    }

    private async Task<IReadOnlyDictionary<string, ToolCallOutcome>> ExecuteCorrectionsAsync(
        IReadOnlyList<AiToolCall> candidates, RejectedCallCorrection correction,
        ProcessUserChatCommand request, ToolExecutionAccumulator results, CancellationToken ct)
    {
        var outcomes = new Dictionary<string, ToolCallOutcome>(StringComparer.Ordinal);
        var bound = await correction.BindAsync(candidates, ai.ToolRegistry, ct);
        foreach (var (candidate, original) in bound)
        {
            ct.ThrowIfCancellationRequested();
            var evidence = original.Outcome.PreparedResponse;
            var corrected = evidence is null ? candidate
                : candidate with { Name = evidence.Request.OperationId, Args = evidence.Intent.ExecutionArguments };
            var outcome = await ExecuteSingleToolCallAsync(corrected, request, results, execution.OperationExecutor,
                execution.PendingClarificationStore, ct, evidence is null ? null : evidence.Request with { Arguments = corrected.Args });
            correction.Accept(original, outcome);
            if (original.Outcome.ActionResult is { } action)
                results.ActionResults.Remove(action);
            if (original.Outcome.OperationResult is { } operation)
                results.OperationResults.Remove(operation);
            if (correction.IsReadOnly && original.Outcome.PolicyDenial is { } denial)
                results.PolicyDenials.Remove(denial);
            results.ToolResults.Remove(original.Outcome.ToolResult);
            if (outcome.ToolResult.Success && !correction.IsReadOnly)
            {
                results.PreviousWriteCalls.Add(corrected);
                results.PreviewedCalls.Add(AgentOperationFingerprint.Compute(corrected.Name, corrected.Args.GetRawText()));
            }
            outcomes[candidate.Id] = outcome;
            if (outcome.OperationResult?.Status == AgentOperationStatus.Failed)
                break;
        }
        return outcomes;
    }
}
