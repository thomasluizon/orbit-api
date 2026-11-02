using FluentAssertions;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Events;

namespace Orbit.Infrastructure.Tests.Events;

public class AccountEventBusScaleTests
{
    [Fact]
    public void SlowSubscriber_CountsEveryDroppedEventAndKeepsTheNewestWindow()
    {
        using var bus = new InMemoryAccountEventBus();
        var userId = Guid.NewGuid();
        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var stream).Should().BeTrue();
        using var lease = stream!.Lease;
        for (var i = 0; i < 300; i++) bus.Publish(userId, new(1, []));
        bus.DroppedEvents(userId).Should().Be(44);
        stream.Reader.TryRead(out var first).Should().BeTrue();
        first!.Id.Should().EndWith(".45");
        bus.TrySubscribe(userId, Guid.NewGuid(), first.Id.Replace(".45", ".1"), out var replay).Should().BeTrue();
        using var replayLease = replay!.Lease;
        replay.Resync.Should().BeTrue();
    }

    [Fact]
    public void CloseSession_FindsTheIndexedStreamAmongTenThousandAccounts()
    {
        using var bus = new InMemoryAccountEventBus();
        for (var i = 0; i < 10_000; i++) bus.Publish(Guid.NewGuid(), new(1, []));
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        bus.TrySubscribe(userId, sessionId, null, out var target).Should().BeTrue();
        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var other).Should().BeTrue();
        using var targetLease = target!.Lease;
        using var otherLease = other!.Lease;
        bus.CloseSessions([sessionId, sessionId]);
        target.SessionClosed.IsCancellationRequested.Should().BeTrue();
        other.SessionClosed.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentSubscriptionsPublishingAndClosing_DoNotLoseTheSessionIndex()
    {
        using var bus = new InMemoryAccountEventBus();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            if (bus.TrySubscribe(userId, sessionId, null, out var stream))
                using (stream!.Lease) { bus.Publish(userId, new(1, [])); bus.CloseSession(sessionId); }
        })));
        bus.TrySubscribe(userId, sessionId, null, out var current).Should().BeTrue();
        using var lease = current!.Lease;
        bus.CloseSession(sessionId);
        current.SessionClosed.IsCancellationRequested.Should().BeTrue();
    }
}
