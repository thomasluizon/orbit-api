using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    internal sealed record CorrectionOccurrence(AiToolCall Call, ToolCallOutcome Outcome);

    private static void OfferCorrections(List<AiToolCallResult> results, IReadOnlyDictionary<string, ToolCallOutcome> outcomes)
    {
        for (var index = 0; index < results.Count; index++)
            if (outcomes.TryGetValue(results[index].Id, out var outcome)
                && outcome.PreparedResponse is RejectedBeforeDispatchResponse { Operation.Status: AgentOperationStatus.Failed })
                results[index] = results[index] with { Payload = new { correction_allowed = true } };
    }

    /// <summary>
    /// Only a unique server-owned occurrence rejected before dispatch can consume one correction.
    /// Its tool must certify the same complete prepared intent across all original occurrences,
    /// including successful siblings. Dispatch uses the original server preparation and request;
    /// a candidate cannot select its parent, change intent, replay an effect or claim an outcome.
    /// </summary>
    private sealed class RejectedCallCorrection(IReadOnlyList<CorrectionOccurrence> occurrences)
    {
        private readonly HashSet<CorrectionOccurrence> _unresolved = occurrences
            .Where(item => item.Outcome.OperationResult?.Status == AgentOperationStatus.Failed).ToHashSet();
        private bool _consumed;

        public bool HasUnresolvedFailures => _unresolved.Count > 0;

        public static bool CanRecover(IReadOnlyList<CorrectionOccurrence> occurrences) =>
            occurrences.Any(item => item.Outcome.OperationResult?.Status == AgentOperationStatus.Failed)
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
            if (outcome.PreparedResponse?.Operation.Status is AgentOperationStatus.Succeeded or AgentOperationStatus.PendingConfirmation)
                _unresolved.Remove(original);
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
            var evidence = original.Outcome.PreparedResponse!;
            var corrected = candidate with { Name = evidence.Request.OperationId, Args = evidence.Intent.ExecutionArguments };
            var outcome = await ExecuteSingleToolCallAsync(corrected, request, results, execution.OperationExecutor,
                execution.PendingClarificationStore, ct, evidence.Request with { Arguments = corrected.Args });
            correction.Accept(original, outcome);
            if (original.Outcome.ActionResult is { } action)
                results.ActionResults.Remove(action);
            if (original.Outcome.OperationResult is { } operation)
                results.OperationResults.Remove(operation);
            results.ToolResults.Remove(original.Outcome.ToolResult);
            if (outcome.ToolResult.Success)
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
