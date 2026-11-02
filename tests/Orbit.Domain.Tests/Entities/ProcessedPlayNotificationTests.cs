using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public sealed class ProcessedPlayNotificationTests
{
    [Fact]
    public void ProcessingLease_OnlyExpiredClaimsCanBeReclaimedOrCompletedByTheirOwner()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var claim = ProcessedPlayNotification.Claim("message", now, TimeSpan.FromMinutes(2));
        var firstOwner = claim.ProcessingLeaseId!.Value;
        claim.TryReclaim(now.AddMinutes(1), TimeSpan.FromMinutes(2)).Should().BeFalse();
        claim.TryReclaim(now.AddMinutes(2), TimeSpan.FromMinutes(2)).Should().BeTrue();
        claim.ProcessingLeaseId.Should().NotBe(firstOwner);
        var completeStale = () => claim.Complete(firstOwner, now.AddMinutes(2));
        completeStale.Should().Throw<InvalidOperationException>();
        claim.Complete(claim.ProcessingLeaseId!.Value, now.AddMinutes(3));
        claim.ProcessedAtUtc.Should().Be(now.AddMinutes(3));
        claim.ProcessingLeaseId.Should().BeNull();
        claim.ProcessingLeaseExpiresAtUtc.Should().BeNull();
        claim.TryReclaim(now.AddMinutes(4), TimeSpan.FromMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void ProcessingLease_InvalidInputsAreRejected()
    {
        var invalidMessage = () => ProcessedPlayNotification.Claim(" ", DateTime.UtcNow, TimeSpan.FromMinutes(2));
        invalidMessage.Should().Throw<ArgumentException>();
        var invalidDuration = () => ProcessedPlayNotification.Claim("message", DateTime.UtcNow, TimeSpan.Zero);
        invalidDuration.Should().Throw<ArgumentOutOfRangeException>();
        var localTime = () => ProcessedPlayNotification.Claim("message", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Unspecified), TimeSpan.FromMinutes(2));
        localTime.Should().Throw<ArgumentException>();
    }

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
