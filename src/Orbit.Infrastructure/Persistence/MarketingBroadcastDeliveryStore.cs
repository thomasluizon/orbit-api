using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public sealed class MarketingBroadcastDeliveryStore(IServiceScopeFactory scopeFactory) : IMarketingBroadcastDeliveryStore
{
    public async Task<IReadOnlySet<Guid>> GetDeliveredRecipientIdsAsync(Guid broadcastId, IReadOnlyCollection<Guid> recipientIds,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        return (await db.MarketingBroadcastDeliveries.AsNoTracking()
            .Where(row => row.BroadcastId == broadcastId && recipientIds.Contains(row.RecipientId))
            .Select(row => row.RecipientId).ToListAsync(cancellationToken)).ToHashSet();
    }

    public async Task RecordDeliveredAsync(Guid broadcastId, Guid recipientId, DateTime deliveredAtUtc, CancellationToken cancellationToken)
    {
        var delivery = MarketingBroadcastDelivery.Create(broadcastId, recipientId, deliveredAtUtc);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MarketingBroadcastDeliveries" ("Id", "BroadcastId", "RecipientId", "DeliveredAtUtc")
            VALUES ({delivery.Id}, {delivery.BroadcastId}, {delivery.RecipientId}, {delivery.DeliveredAtUtc})
            ON CONFLICT ("BroadcastId", "RecipientId") DO NOTHING
            """, cancellationToken);
    }
}
