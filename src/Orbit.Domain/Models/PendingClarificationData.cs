namespace Orbit.Domain.Models;

/// <summary>
/// Data needed to resolve a pending clarification: the original tool name, the partial
/// arguments JSON, and the key of the missing argument that the user just supplied.
/// Returned by <c>IPendingClarificationStore.GetForResolutionAsync</c>.
/// </summary>
public record PendingClarificationData(
    string ToolName,
    string PartialArgumentsJson,
    string MissingArgumentKey);
