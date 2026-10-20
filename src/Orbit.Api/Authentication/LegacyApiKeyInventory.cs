using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Authentication;

public sealed class LegacyApiKeyInventory(IServiceScopeFactory scopeFactory, ILogger<LegacyApiKeyInventory> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGenericRepository<ApiKey>>();
        var count = await repository.CountAsync(key => key.KeyHash.StartsWith("$2"), cancellationToken);
        logger.LogInformation("Legacy BCrypt API key hashes remaining: {LegacyHashCount}", count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
