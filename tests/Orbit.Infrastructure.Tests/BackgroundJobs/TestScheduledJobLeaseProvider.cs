using System.Collections.Concurrent;
using Orbit.Infrastructure.BackgroundJobs;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

internal sealed class TestScheduledJobLeaseProvider : IScheduledJobLeaseProvider
{
    private readonly ConcurrentDictionary<string, byte> _held = new();
    public Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAsyncDisposable?>(_held.TryAdd(jobName, 0)
            ? new Lease(() => _held.TryRemove(jobName, out _)) : null);
    }
    private sealed class Lease(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
    }
}
