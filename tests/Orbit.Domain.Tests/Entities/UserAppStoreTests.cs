using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class UserAppStoreTests
{
    [Fact]
    public void Subscription_GrantRevokeAndStaleUpdate_PreserveBinding()
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        var now = DateTime.UtcNow;
        user.ApplyAppStoreSubscription("original", "Sandbox", now.AddDays(30), SubscriptionInterval.Monthly, now, true, null);
        user.HasActivePaidSubscription.Should().BeTrue();
        user.ApplyAppStoreSubscription("original", "Sandbox", now.AddDays(30), SubscriptionInterval.Monthly, now.AddSeconds(1), false, SubscriptionLapseReason.Expired);
        user.HasProAccess.Should().BeFalse();
        user.AppStoreOriginalTransactionId.Should().Be("original");
        user.ApplyAppStoreSubscription("original", "Sandbox", now.AddDays(30), SubscriptionInterval.Monthly, now, true, null);
        user.HasProAccess.Should().BeFalse();
    }

    [Fact]
    public void Subscription_StripeCoversLaterPeriod_KeepsStripe()
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        var now = DateTime.UtcNow;
        user.SetStripeSubscription("sub", now.AddDays(60));
        user.ApplyAppStoreSubscription("original", "Production", now.AddDays(30), SubscriptionInterval.Monthly, now, true, null);
        user.SubscriptionSource.Should().Be(SubscriptionSource.Stripe);
        user.SubscriptionExpiresAtUtc.Should().Be(now.AddDays(60));
        user.AppStoreOriginalTransactionId.Should().Be("original");
    }

    [Theory]
    [InlineData("", "Sandbox")]
    [InlineData("original", "Xcode")]
    public void Link_InvalidBinding_Rejects(string original, string environment)
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        var act = () => user.LinkAppStoreSubscription(original, environment);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeletionCancellation_DoesNotSupportAppStore()
    {
        var act = () => OwedSubscriptionCancellation.Create(SubscriptionSource.AppStore, "original");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
