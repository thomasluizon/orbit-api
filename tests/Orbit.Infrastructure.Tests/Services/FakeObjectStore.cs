using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Services;

internal sealed class FakeObjectStore : IObjectStorageDeletionService
{
    public HashSet<string> Keys { get; } = [];
    public int FailuresRemaining { get; set; }
    public int Attempts { get; private set; }
    public Action? BeforeDelete { get; set; }

    public Task DeleteUserObjectsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Attempts++;
        BeforeDelete?.Invoke();
        if (FailuresRemaining-- > 0)
            throw new HttpRequestException("Storage unavailable");
        Keys.RemoveWhere(key => key.StartsWith($"{userId}/", StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}
