using System.Text.Json.Serialization;

namespace Orbit.Application.Chat.Models;

/// <summary>
/// Request body for <c>POST /api/ai/clarifications/{operationId}/resolve</c>.
/// <c>Value</c> is a JSON-encoded merge patch (e.g. <c>{"frequency_unit":"Day","frequency_quantity":1}</c>)
/// that gets shallow-merged into the stashed partial arguments before the original tool re-runs.
/// </summary>
public record ResolveClarificationRequest([property: JsonRequired] string Value);
