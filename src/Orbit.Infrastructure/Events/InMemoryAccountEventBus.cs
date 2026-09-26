using System.Threading.Channels;
using Orbit.Domain.Events;

namespace Orbit.Infrastructure.Events;

public sealed class InMemoryAccountEventBus : IAccountEventBus
{
    private const int MaxStreamsPerAccount = 3;
    private const int BufferSize = 256;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AccountState> _accounts = [];
    private DateTimeOffset _nextSweepAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);

    public bool TrySubscribe(Guid userId, Guid sessionId, string? lastEventId, out AccountEventSubscription? subscription)
    {
        lock (_gate)
        {
            Prune();
            if (!_accounts.TryGetValue(userId, out var state))
                _accounts[userId] = state = new AccountState();
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
                if (position >= 0)
                    replay = state.Buffer.Skip(position + 1).ToArray();
                else
                    resync = true;
            }

            var channel = Channel.CreateBounded<PublishedAccountEvent>(new BoundedChannelOptions(BufferSize)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            var subscriber = new Subscriber(sessionId, channel);
            state.Subscribers.Add(subscriber);
            subscription = new AccountEventSubscription(
                Guid.NewGuid().ToString("N"), replay, resync, channel.Reader,
                subscriber.Closed.Token, new Lease(this, userId, subscriber));
            return true;
        }
    }

    public void Publish(Guid userId, AccountEventPayload payload, bool resync = false)
    {
        lock (_gate)
        {
            Prune();
            if (!_accounts.TryGetValue(userId, out var state))
                _accounts[userId] = state = new AccountState();
            state.LastTouchedAtUtc = DateTimeOffset.UtcNow;

            var item = new PublishedAccountEvent($"{state.Epoch}.{++state.Sequence}", resync ? "resync" : "changes", payload);
            state.Buffer.Add(item);
            if (state.Buffer.Count > BufferSize)
                state.Buffer.RemoveAt(0);

            foreach (var subscriber in state.Subscribers)
                subscriber.Channel.Writer.TryWrite(item);
        }
    }

    public void CloseSession(Guid sessionId)
    {
        lock (_gate)
        {
            foreach (var subscriber in _accounts.Values.SelectMany(state => state.Subscribers))
                if (subscriber.SessionId == sessionId)
                    subscriber.Closed.Cancel();
        }
    }

    public void CloseAccount(Guid userId)
    {
        lock (_gate)
        {
            if (_accounts.TryGetValue(userId, out var state))
                foreach (var subscriber in state.Subscribers)
                    subscriber.Closed.Cancel();
        }
    }

    private void Unsubscribe(Guid userId, Subscriber subscriber)
    {
        lock (_gate)
        {
            if (_accounts.TryGetValue(userId, out var state))
            {
                state.Subscribers.Remove(subscriber);
                state.LastTouchedAtUtc = DateTimeOffset.UtcNow;
            }
            subscriber.Channel.Writer.TryComplete();
            subscriber.Closed.Dispose();
        }
    }

    private sealed class AccountState
    {
        public string Epoch { get; } = Guid.NewGuid().ToString("N");
        public long Sequence;
        public DateTimeOffset LastTouchedAtUtc = DateTimeOffset.UtcNow;
        public List<PublishedAccountEvent> Buffer { get; } = [];
        public List<Subscriber> Subscribers { get; } = [];
    }

    private void Prune()
    {
        var nowAtUtc = DateTimeOffset.UtcNow;
        if (nowAtUtc < _nextSweepAtUtc)
            return;

        foreach (var userId in _accounts
            .Where(pair => pair.Value.Subscribers.Count == 0
                && nowAtUtc - pair.Value.LastTouchedAtUtc > TimeSpan.FromMinutes(30))
            .Select(pair => pair.Key).ToArray())
            _accounts.Remove(userId);

        _nextSweepAtUtc = nowAtUtc.AddMinutes(5);
    }

    private sealed record Subscriber(Guid SessionId, Channel<PublishedAccountEvent> Channel)
    {
        public CancellationTokenSource Closed { get; } = new();
    }

    private sealed class Lease(InMemoryAccountEventBus bus, Guid userId, Subscriber subscriber) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                bus.Unsubscribe(userId, subscriber);
        }
    }
}
