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

#pragma warning disable CS0649 // Written only at the SQL layer via ExecuteUpdate and read back via reflection on materialization; there is no C# writer, which is what removes the S1144 unused-private-setter finding. https://github.com/thomasluizon/orbit-api/pull/390
    private DateTime? _resolvedAtUtc;
#pragma warning restore CS0649

    /// <summary>
    /// UTC instant this clarification was resolved; null while still open. Exposed as a read-only
    /// property over an explicitly-mapped backing field (see ConfigurePendingClarificationEntity) so
    /// the column stays mapped without a private setter -- read-only auto-properties get dropped by
    /// EF convention. The one-shot resolve flips it atomically via ExecuteUpdate. https://github.com/thomasluizon/orbit-api/pull/390
    /// </summary>
    public DateTime? ResolvedAtUtc => _resolvedAtUtc;

    private PendingClarification() { }

    public static PendingClarification Create(
        Guid userId,
        string toolName,
        string partialArgumentsJson,
        string missingArgumentKey,
        string question,
        string quickActionsJson,
        DateTime expiresAtUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("userId cannot be empty.", nameof(userId));
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("toolName is required.", nameof(toolName));
        if (string.IsNullOrWhiteSpace(missingArgumentKey))
            throw new ArgumentException("missingArgumentKey is required.", nameof(missingArgumentKey));
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("question is required.", nameof(question));

        var createdAtUtc = DateTime.UtcNow;
        if (expiresAtUtc <= createdAtUtc)
            throw new ArgumentException("expiresAtUtc must be in the future.", nameof(expiresAtUtc));

        return new PendingClarification
        {
            UserId = userId,
            ToolName = toolName,
            PartialArgumentsJson = string.IsNullOrWhiteSpace(partialArgumentsJson) ? "{}" : partialArgumentsJson,
            MissingArgumentKey = missingArgumentKey,
            Question = question,
            QuickActionsJson = string.IsNullOrWhiteSpace(quickActionsJson) ? "[]" : quickActionsJson,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAtUtc;

    public bool IsResolved => ResolvedAtUtc.HasValue;
}
