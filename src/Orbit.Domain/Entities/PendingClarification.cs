using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// Server-side stash for a tool call that returned NeedsClarification — holds the partial
/// arguments and a description of the missing field. Resolved by POST /api/ai/clarifications/{id}/resolve,
/// which merges the user's chosen value into the partial args and re-invokes the original tool.
/// One-shot: <see cref="ResolvedAtUtc"/> is set on resolution; subsequent resolves are rejected.
/// </summary>
public class PendingClarification : Entity
{
    public Guid UserId { get; private set; }
    public string ToolName { get; private set; } = null!;
    public string PartialArgumentsJson { get; private set; } = "{}";
    public string MissingArgumentKey { get; private set; } = null!;
    public string Question { get; private set; } = null!;
    public string QuickActionsJson { get; private set; } = "[]";
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? ResolvedAtUtc { get; private set; }

    private PendingClarification()
    {
    }

    public static PendingClarification Create(
        Guid userId,
        string toolName,
        string partialArgumentsJson,
        string missingArgumentKey,
        string question,
        string quickActionsJson,
        DateTime expiresAtUtc)
    {
        return new PendingClarification
        {
            UserId = userId,
            ToolName = toolName,
            PartialArgumentsJson = string.IsNullOrWhiteSpace(partialArgumentsJson) ? "{}" : partialArgumentsJson,
            MissingArgumentKey = missingArgumentKey,
            Question = question,
            QuickActionsJson = string.IsNullOrWhiteSpace(quickActionsJson) ? "[]" : quickActionsJson,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAtUtc;

    public bool IsResolved => ResolvedAtUtc.HasValue;

    public void MarkResolved()
    {
        ResolvedAtUtc = DateTime.UtcNow;
    }
}
