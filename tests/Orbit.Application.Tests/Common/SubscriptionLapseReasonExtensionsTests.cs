using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Common;

public class SubscriptionLapseReasonExtensionsTests
{
    [Theory]
    [InlineData(SubscriptionLapseReason.Canceled, "canceled")]
    [InlineData(SubscriptionLapseReason.PaymentFailed, "payment_failed")]
    [InlineData(SubscriptionLapseReason.Expired, "expired")]
    public void ToApiValue_KnownReason_ReturnsContractValue(
        SubscriptionLapseReason reason, string expected)
    {
        ((SubscriptionLapseReason?)reason).ToApiValue().Should().Be(expected);
    }

    [Fact]
    public void ToApiValue_UnknownReason_ReturnsNull()
    {
        ((SubscriptionLapseReason?)999).ToApiValue().Should().BeNull();
    }
}
