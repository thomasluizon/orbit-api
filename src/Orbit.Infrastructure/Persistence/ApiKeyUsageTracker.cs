using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class ApiKeyUsageTracker(OrbitDbContext db, TimeProvider timeProvider, ILogger<ApiKeyUsageTracker> logger) : IApiKeyUsageTracker
{
    public async Task MarkUsedAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        var usedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var cutoffAtUtc = usedAtUtc.AddMinutes(-1);
        try
        {
            await db.ApiKeys.Where(key => key.Id == keyId && (key.LastUsedAtUtc == null || key.LastUsedAtUtc <= cutoffAtUtc))
                .ExecuteUpdateAsync(setters => setters.SetProperty(key => key.LastUsedAtUtc, usedAtUtc), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not record API key usage for {KeyId}", keyId);
        }
    }
}
