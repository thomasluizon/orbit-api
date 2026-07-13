using System.Text.Json;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    internal sealed class ToolExecutionAccumulator
    {
        private readonly List<string> _relatedSurfaces = [];
        private readonly HashSet<string> _seenRelatedSurfaces = new(StringComparer.Ordinal);
        private readonly HashSet<string> _calledToolNames = new(StringComparer.Ordinal);

        public List<ActionResult> ActionResults { get; } = [];
        public List<AgentOperationResult> OperationResults { get; } = [];
        public List<PendingAgentOperation> PendingOperations { get; } = [];
        public List<AgentPolicyDenial> PolicyDenials { get; } = [];

        /// <summary>
        /// Distinct names of every tool invoked this turn, used to decide whether the turn is safe to
        /// serve from the shared FAQ cache (only static, user-data-free tools may have run).
        /// </summary>
        public IReadOnlyCollection<string> CalledToolNames => _calledToolNames;

        /// <summary>
        /// App surface IDs (e.g. "today", "gamification") surfaced by read-only tools such as
        /// describe_feature, deduplicated in first-seen order. The client maps these to deep links.
        /// </summary>
        public IReadOnlyList<string> RelatedSurfaces => _relatedSurfaces;

        public void Add(
            string toolName,
            ActionResult? actionResult,
            AgentOperationResult? operationResult,
            AgentPolicyDenial? policyDenial,
            PendingAgentOperation? pendingOperation)
        {
            _calledToolNames.Add(toolName);

            if (actionResult is not null)
                ActionResults.Add(actionResult);

            if (operationResult is not null)
            {
                OperationResults.Add(operationResult);
                CollectRelatedSurfaces(operationResult);
            }

            if (policyDenial is not null)
                PolicyDenials.Add(policyDenial);

            if (pendingOperation is not null)
                PendingOperations.Add(pendingOperation);
        }

        private void CollectRelatedSurfaces(AgentOperationResult operationResult)
        {
            if (operationResult.Status != AgentOperationStatus.Succeeded)
                return;

            foreach (var surface in ExtractRelatedSurfaces(operationResult.Payload))
            {
                if (_seenRelatedSurfaces.Add(surface))
                    _relatedSurfaces.Add(surface);
            }
        }
    }

    /// <summary>
    /// Reads the optional "related_surfaces" string array from a tool's anonymous payload
    /// (e.g. describe_feature) by round-tripping it through JSON. Returns an empty sequence
    /// when the payload is null, not an object, or carries no usable surface IDs.
    /// </summary>
    private static List<string> ExtractRelatedSurfaces(object? payload)
    {
        if (payload is null)
            return [];

        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(payload);
        }
        catch (NotSupportedException)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("related_surfaces", out var surfaces)
            || surfaces.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return surfaces
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }
}
