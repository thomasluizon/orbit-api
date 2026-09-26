namespace Orbit.Domain.Events;

public sealed record AccountChange(string Kind, string Op, IReadOnlyList<Guid> Ids, IReadOnlyList<DateOnly>? Dates = null);

public sealed record AccountEventPayload(int V, IReadOnlyList<AccountChange> Changes, string? Origin = null);

public sealed record PublishedAccountEvent(string Id, string Type, AccountEventPayload Data);

public sealed record AccountEventSubscription(
    string ConnectionId,
    IReadOnlyList<PublishedAccountEvent> Replay,
    bool Resync,
    System.Threading.Channels.ChannelReader<PublishedAccountEvent> Reader,
    CancellationToken SessionClosed,
    IDisposable Lease);

/// <summary>
/// This process-local transport needs a backplane before a second API instance is started.
/// A Postgres LISTEN connection through the session pooler or Render Key Value can carry it.
/// </summary>
public interface IAccountEventBus
{
    bool TrySubscribe(Guid userId, Guid sessionId, string? lastEventId, out AccountEventSubscription? subscription);
    void Publish(Guid userId, AccountEventPayload payload, bool resync = false);
    void CloseSession(Guid sessionId);
    void CloseAccount(Guid userId);
}

public interface IAccountEventCollector
{
    void MarkResync(Guid userId);
    void Clear();
}
