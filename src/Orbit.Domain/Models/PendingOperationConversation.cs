using System.Text.Json;

namespace Orbit.Domain.Models;

public sealed record PendingOperationConversation
{
    public Guid Id { get; }
    public DateTime StartedAtUtc { get; }
    public bool UsesServerHistory { get; }
    public bool ExistingConversation { get; }

    public PendingOperationConversation(Guid id, DateTime startedAtUtc, bool usesServerHistory = false, bool existingConversation = false)
    {
        if (id == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (startedAtUtc == default || startedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentOutOfRangeException(nameof(startedAtUtc));
        if (existingConversation && !usesServerHistory)
            throw new ArgumentException("An existing stored conversation requires server history.", nameof(existingConversation));
        Id = id;
        StartedAtUtc = startedAtUtc;
        UsesServerHistory = usesServerHistory;
        ExistingConversation = existingConversation;
    }
}

public sealed record PendingAgentOperationPreview(PendingAgentOperation Proposal, string Outcome, int Revision,
    JsonElement CurrentArguments);
