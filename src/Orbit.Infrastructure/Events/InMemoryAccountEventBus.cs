using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Domain.Events;

namespace Orbit.Infrastructure.Events;

public sealed class InMemoryAccountEventBus : IAccountEventBus, IDisposable
{
    private const int MaxStreamsPerAccount = 3;
    private const int BufferSize = 256;
    private readonly ConcurrentDictionary<Guid, AccountState> _accounts = new();
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();
    private readonly Timer _sweep;
    private readonly ILogger<InMemoryAccountEventBus> _logger;

    public InMemoryAccountEventBus(ILogger<InMemoryAccountEventBus>? logger = null)
    {
        _logger = logger ?? NullLogger<InMemoryAccountEventBus>.Instance;
        _sweep = new Timer(_ => Prune(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public bool TrySubscribe(Guid userId, Guid sessionId, string? lastEventId, out AccountEventSubscription? subscription)
    {
        while (true)
        {
            var state = _accounts.GetOrAdd(userId, _ => new AccountState());
            lock (state.Gate)
            {
                if (state.Retired) continue;
                state.LastTouchedAtUtc = DateTimeOffset.UtcNow;
                if (state.Subscribers.Count >= MaxStreamsPerAccount)
                {
                    subscription = null;
                    return false;
                }
                var replay = Array.Empty<PublishedAccountEvent>();
                var resync = false;
                if (!string.IsNullOrEmpty(lastEventId))
                {
                    var position = state.Buffer.FindIndex(item => item.Id == lastEventId);
                    if (position >= 0) replay = state.Buffer.Skip(position + 1).ToArray();
                    else resync = true;
                }
                var channel = Channel.CreateBounded<PublishedAccountEvent>(new BoundedChannelOptions(BufferSize)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });
                var subscriber = new Subscriber(sessionId, channel);
                state.Subscribers.Add(subscriber);
                while (true)
                {
                    var indexed = _sessions.GetOrAdd(sessionId, _ => new());
                    lock (indexed.Gate)
                    {
                        if (indexed.Retired) continue;
                        indexed.Subscribers.Add(subscriber);
                        break;
                    }
                }
                subscription = new AccountEventSubscription(Guid.NewGuid().ToString("N"), replay, resync,
                    channel.Reader, subscriber.Closed.Token, new Lease(this, userId, subscriber));
                return true;
            }
        }
    }

    public void Publish(Guid userId, AccountEventPayload payload, bool resync = false)
    {
        while (true)
        {
            var state = _accounts.GetOrAdd(userId, _ => new AccountState());
            lock (state.Gate)
            {
                if (state.Retired) continue;
                state.LastTouchedAtUtc = DateTimeOffset.UtcNow;
                var item = new PublishedAccountEvent($"{state.Epoch}.{++state.Sequence}", resync ? "resync" : "changes", payload);
                state.Buffer.Add(item);
                if (state.Buffer.Count > BufferSize) state.Buffer.RemoveAt(0);
                foreach (var subscriber in state.Subscribers)
                {
                    if (subscriber.Channel.Writer.TryWrite(item)) continue;
                    if (subscriber.Channel.Reader.TryRead(out _))
                    {
                        state.DroppedEvents++;
                        _logger.LogDebug("Account event dropped for {UserId}; total {DroppedCount}", userId, state.DroppedEvents);
                    }
                    subscriber.Channel.Writer.TryWrite(item);
                }
                return;
            }
        }
    }

    public void CloseSession(Guid sessionId) => CloseSessions([sessionId]);

    public void CloseSessions(IReadOnlyCollection<Guid> sessionIds)
    {
        var subscribers = new List<Subscriber>();
        foreach (var id in sessionIds.Distinct())
            if (_sessions.TryGetValue(id, out var indexed))
                lock (indexed.Gate) subscribers.AddRange(indexed.Subscribers);
        foreach (var subscriber in subscribers) subscriber.Close();
    }

    public void CloseAccount(Guid userId)
    {
        if (!_accounts.TryGetValue(userId, out var state)) return;
        Subscriber[] subscribers;
        lock (state.Gate) subscribers = state.Subscribers.ToArray();
        foreach (var subscriber in subscribers) subscriber.Close();
    }

    internal long DroppedEvents(Guid userId)
    {
        if (!_accounts.TryGetValue(userId, out var state)) return 0;
        lock (state.Gate) return state.DroppedEvents;
    }

    private void Unsubscribe(Guid userId, Subscriber subscriber)
    {
        if (_accounts.TryGetValue(userId, out var state))
        {
            lock (state.Gate)
            {
                state.Subscribers.Remove(subscriber);
                state.LastTouchedAtUtc = DateTimeOffset.UtcNow;
                if (_sessions.TryGetValue(subscriber.SessionId, out var indexed))
                {
                    lock (indexed.Gate)
                    {
                        indexed.Subscribers.Remove(subscriber);
                        if (indexed.Subscribers.Count == 0)
                        {
                            indexed.Retired = true;
                            ((ICollection<KeyValuePair<Guid, SessionState>>)_sessions)
                                .Remove(new(subscriber.SessionId, indexed));
                        }
                    }
                }
            }
        }
        subscriber.Channel.Writer.TryComplete();
        subscriber.Dispose();
    }

    private void Prune()
    {
        var nowAtUtc = DateTimeOffset.UtcNow;
        foreach (var pair in _accounts)
            lock (pair.Value.Gate)
                if (pair.Value.Subscribers.Count == 0 && nowAtUtc - pair.Value.LastTouchedAtUtc > TimeSpan.FromMinutes(30))
                {
                    pair.Value.Retired = true;
                    ((ICollection<KeyValuePair<Guid, AccountState>>)_accounts).Remove(pair);
                }
    }

    public void Dispose()
    {
        _sweep.Dispose();
        foreach (var userId in _accounts.Keys) CloseAccount(userId);
    }

    private sealed class SessionState
    {
        public object Gate { get; } = new();
        public bool Retired;
        public HashSet<Subscriber> Subscribers { get; } = [];
    }

    private sealed class AccountState
    {
        public object Gate { get; } = new();
        public bool Retired;
        public string Epoch { get; } = Guid.NewGuid().ToString("N");
        public long Sequence;
        public long DroppedEvents;
        public DateTimeOffset LastTouchedAtUtc = DateTimeOffset.UtcNow;
        public List<PublishedAccountEvent> Buffer { get; } = [];
        public List<Subscriber> Subscribers { get; } = [];
    }

    private sealed record Subscriber(Guid SessionId, Channel<PublishedAccountEvent> Channel) : IDisposable
    {
        private readonly object _gate = new();
        private bool _disposed;
        public CancellationTokenSource Closed { get; } = new();
        public void Close() { lock (_gate) if (!_disposed) Closed.Cancel(); }
        public void Dispose() { lock (_gate) { _disposed = true; Closed.Dispose(); } }
    }

    private sealed class Lease(InMemoryAccountEventBus bus, Guid userId, Subscriber subscriber) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) bus.Unsubscribe(userId, subscriber);
        }
    }
}
