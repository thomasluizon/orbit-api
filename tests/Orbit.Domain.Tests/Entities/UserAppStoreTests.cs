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
    [InlineData(SubscriptionSource.Stripe)]
    [InlineData(SubscriptionSource.GooglePlay)]
    public void ShorterApplePurchaseAndRefund_PreserveOtherProvider(SubscriptionSource provider)
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        var now = DateTime.UtcNow;
        var paidThrough = now.AddDays(365);
        if (provider == SubscriptionSource.Stripe)
            user.SetStripeSubscription("stripe", paidThrough, SubscriptionInterval.Yearly);
        else
            user.SetPlaySubscription("play", paidThrough, SubscriptionInterval.Yearly);

        user.ApplyAppStoreSubscription("original", "Sandbox", now.AddDays(30), SubscriptionInterval.Monthly, now, true, null);
        user.SubscriptionSource.Should().Be(provider);
        user.SubscriptionExpiresAtUtc.Should().Be(paidThrough);
        user.SubscriptionInterval.Should().Be(SubscriptionInterval.Yearly);
        user.AppStoreOriginalTransactionId.Should().Be("original");

        user.ApplyAppStoreSubscription("original", "Sandbox", now.AddDays(30), SubscriptionInterval.Monthly,
            now.AddMinutes(1), false, SubscriptionLapseReason.Expired);
        user.HasProAccess.Should().BeTrue();
        user.SubscriptionSource.Should().Be(provider);
        user.SubscriptionExpiresAtUtc.Should().Be(paidThrough);
    }

    [Theory]
    [InlineData(SubscriptionSource.Stripe)]
    [InlineData(SubscriptionSource.GooglePlay)]
    public void ShorterProviderRefreshAndCancellation_PreserveApple(SubscriptionSource provider)
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        var now = DateTime.UtcNow;
        if (provider == SubscriptionSource.Stripe)
            user.SetStripeSubscription("stripe", now.AddDays(30));
        else
            user.SetPlaySubscription("play", now.AddDays(30));
        var paidThrough = now.AddDays(365);
        user.ApplyAppStoreSubscription("original", "Sandbox", paidThrough, SubscriptionInterval.Yearly, now, true, null);

        if (provider == SubscriptionSource.Stripe)
            user.SetStripeSubscription("stripe", now.AddDays(30), SubscriptionInterval.Monthly, now.AddMinutes(1));
        else
            user.SetPlaySubscription("play", now.AddDays(30), SubscriptionInterval.Monthly);
        user.SubscriptionSource.Should().Be(SubscriptionSource.AppStore);
        user.SubscriptionExpiresAtUtc.Should().Be(paidThrough);
        user.SubscriptionInterval.Should().Be(SubscriptionInterval.Yearly);
        if (provider == SubscriptionSource.Stripe)
            user.CancelStripeSubscription(SubscriptionLapseReason.Canceled, now.AddMinutes(2));
        else
            user.CancelPlaySubscription(SubscriptionLapseReason.Canceled);
        user.HasProAccess.Should().BeTrue();
        user.SubscriptionSource.Should().Be(SubscriptionSource.AppStore);
        user.SubscriptionExpiresAtUtc.Should().Be(paidThrough);
    }

    [Theory]
    [InlineData(SubscriptionSource.Stripe, true)]
    [InlineData(SubscriptionSource.GooglePlay, true)]
    [InlineData(SubscriptionSource.Stripe, false)]
    [InlineData(SubscriptionSource.GooglePlay, false)]
    public void RevokingLongerProvider_FallsBackToOtherPaidCoverage(SubscriptionSource provider, bool appleIsLonger)
    {
        var user = User.Create("Apple", "apple@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        var now = DateTime.UtcNow;
        var appleExpiry = now.AddDays(appleIsLonger ? 365 : 30);
        var otherExpiry = now.AddDays(appleIsLonger ? 30 : 365);
        user.ApplyAppStoreSubscription("original", "Sandbox", appleExpiry, SubscriptionInterval.Yearly, now, true, null);
        if (provider == SubscriptionSource.Stripe)
            user.SetStripeSubscription("stripe", otherExpiry, SubscriptionInterval.Monthly);
        else
            user.SetPlaySubscription("play", otherExpiry, SubscriptionInterval.Monthly, isInGracePeriod: true);
        if (appleIsLonger)
            user.ApplyAppStoreSubscription("original", "Sandbox", appleExpiry, SubscriptionInterval.Yearly,
                now.AddMinutes(1), false, SubscriptionLapseReason.Expired);
        else if (provider == SubscriptionSource.Stripe)
            user.CancelStripeSubscription(SubscriptionLapseReason.Canceled);
        else
            user.CancelPlaySubscription(SubscriptionLapseReason.Canceled);
        user.HasProAccess.Should().BeTrue();
        user.SubscriptionSource.Should().Be(appleIsLonger ? provider : SubscriptionSource.AppStore);
        user.SubscriptionExpiresAtUtc.Should().Be(now.AddDays(30));
        user.SubscriptionInterval.Should().Be(appleIsLonger ? SubscriptionInterval.Monthly : SubscriptionInterval.Yearly);
        if (appleIsLonger && provider == SubscriptionSource.GooglePlay)
            user.SubscriptionLapseReason.Should().Be(SubscriptionLapseReason.PaymentFailed);
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
