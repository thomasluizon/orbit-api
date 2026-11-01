using System.Text.Json;

namespace Orbit.Domain.Models;

public sealed record PendingOperationConversation
{
    public Guid Id { get; }
    public DateTime StartedAtUtc { get; }

    public PendingOperationConversation(Guid id, DateTime startedAtUtc)
    {
        if (id == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (startedAtUtc == default || startedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentOutOfRangeException(nameof(startedAtUtc));
        Id = id;
        StartedAtUtc = startedAtUtc;
    }
}

public sealed record PendingAgentOperationPreview(PendingAgentOperation Proposal, string Outcome, int Revision,
    JsonElement CurrentArguments);
