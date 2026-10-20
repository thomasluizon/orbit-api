using System.Security.Cryptography;
using System.Text;

namespace Orbit.Infrastructure.Services.Notion;

internal interface INotionDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken ct);
}

internal sealed class NotionDelay : INotionDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

internal sealed class NotionRequestScheduler(INotionDelay delay)
{
    private sealed class QueueEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private readonly Dictionary<string, QueueEntry> _queues = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public async Task<T> RunAsync<T>(string credential, Func<Task<T>> operation, CancellationToken ct)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
        QueueEntry entry;
        lock (_sync)
        {
            if (!_queues.TryGetValue(key, out var existing)) _queues[key] = existing = new QueueEntry();
            entry = existing;
            entry.Users++;
        }
        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(ct);
            acquired = true;
            await delay.WaitAsync(TimeSpan.FromMilliseconds(350), ct);
            return await operation();
        }
        finally
        {
            if (acquired) entry.Semaphore.Release();
            lock (_sync)
            {
                if (--entry.Users == 0)
                {
                    _queues.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }
}
