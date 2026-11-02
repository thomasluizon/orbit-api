using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public sealed class ProcessedPlayNotificationTests
{
    [Fact]
    public void CouponIntent_RemainsUntilQueueAcceptsIt()
    {
        var notification = ProcessedPlayNotification.Create("message");
        var user = Guid.NewGuid();
        notification.RecordConsumedCoupon(user, "coupon");
        notification.CouponUserId.Should().Be(user);
        notification.ConsumedCouponId.Should().Be("coupon");
        notification.MarkCouponQueued();
        notification.CouponUserId.Should().BeNull();
        notification.ConsumedCouponId.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void InvalidCoupon_DoesNotMutateIntent(string? coupon)
    {
        var notification = ProcessedPlayNotification.Create("message");
        var mutate = () => notification.RecordConsumedCoupon(Guid.NewGuid(), coupon!);
        mutate.Should().Throw<ArgumentException>();
        notification.CouponUserId.Should().BeNull();
    }

    [Fact]
    public void EmptyUser_DoesNotMutateIntent()
    {
        var notification = ProcessedPlayNotification.Create("message");
        var mutate = () => notification.RecordConsumedCoupon(Guid.Empty, "coupon");
        mutate.Should().Throw<ArgumentException>();
        notification.ConsumedCouponId.Should().BeNull();
    }
}
