using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public sealed class MarketingBroadcastDeliveryTests
{
    [Fact]
    public void Create_PreservesBroadcastRecipientAndDeliveryInstant()
    {
        var broadcastId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var deliveredAtUtc = new DateTime(2030, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var delivery = MarketingBroadcastDelivery.Create(broadcastId, recipientId, deliveredAtUtc);

        delivery.BroadcastId.Should().Be(broadcastId);
        delivery.RecipientId.Should().Be(recipientId);
        delivery.DeliveredAtUtc.Should().Be(deliveredAtUtc);
    }

    [Fact]
    public void Create_RejectsMissingIdentitiesAndNonUtcInstants()
    {
        var missingBroadcast = () => MarketingBroadcastDelivery.Create(Guid.Empty, Guid.NewGuid(), DateTime.UtcNow);
        var missingRecipient = () => MarketingBroadcastDelivery.Create(Guid.NewGuid(), Guid.Empty, DateTime.UtcNow);
        var localTime = () => MarketingBroadcastDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), DateTime.Now);
        var missingTime = () => MarketingBroadcastDelivery.Create(Guid.NewGuid(), Guid.NewGuid(), default);

        missingBroadcast.Should().Throw<ArgumentException>();
        missingRecipient.Should().Throw<ArgumentException>();
        localTime.Should().Throw<ArgumentException>();
        missingTime.Should().Throw<ArgumentException>();
    }
}
