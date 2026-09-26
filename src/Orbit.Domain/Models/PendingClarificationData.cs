namespace Orbit.Domain.Models;

public record PendingClarificationData(
    string ToolName,
    string PartialArgumentsJson,
    string MissingArgumentKey,
    IReadOnlyList<string> AllowedValues,
    DateTime ExpiresAtUtc);
