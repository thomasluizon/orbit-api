namespace Orbit.Domain.Models;

/// <summary>
/// Data needed to resolve a pending clarification: the original tool name, the partial
/// arguments JSON, the key of the missing argument that the user just supplied, and the
/// set of quick-action values the server originally offered. <c>AllowedValues</c> is the
/// allowlist of acceptable patch payloads — anything else gets rejected before the merge,
/// so a malicious client can't override arbitrary fields by hand-crafting the request.
/// Returned by <c>IPendingClarificationStore.GetForResolutionAsync</c>.
/// </summary>
public record PendingClarificationData(
    string ToolName,
    string PartialArgumentsJson,
    string MissingArgumentKey,
    IReadOnlyList<string> AllowedValues,
    DateTime ExpiresAtUtc);
