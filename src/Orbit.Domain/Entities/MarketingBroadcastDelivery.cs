using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class MarketingBroadcastDelivery : Entity
{
    public Guid BroadcastId { get; private set; }
    public Guid RecipientId { get; private set; }
    public DateTime DeliveredAtUtc { get; private set; }

    private MarketingBroadcastDelivery() { }

    public static MarketingBroadcastDelivery Create(Guid broadcastId, Guid recipientId, DateTime deliveredAtUtc)
    {
        if (broadcastId == Guid.Empty) throw new ArgumentException("Broadcast is required.", nameof(broadcastId));
        if (recipientId == Guid.Empty) throw new ArgumentException("Recipient is required.", nameof(recipientId));
        if (deliveredAtUtc.Kind != DateTimeKind.Utc || deliveredAtUtc == default)
            throw new ArgumentException("Delivery time must be a UTC instant.", nameof(deliveredAtUtc));
        return new MarketingBroadcastDelivery { BroadcastId = broadcastId, RecipientId = recipientId, DeliveredAtUtc = deliveredAtUtc };
    }
}
