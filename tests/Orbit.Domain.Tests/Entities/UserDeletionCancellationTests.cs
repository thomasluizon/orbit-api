using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class UserDeletionCancellationTests
{
    [Theory]
    [InlineData(SubscriptionSource.Stripe)]
    [InlineData(SubscriptionSource.GooglePlay)]
    public void CancellationOwnership_SurvivesReactivationAndSameSubscriptionWebhook(SubscriptionSource source)
    {
        var user = CreatePaidUser();
        var identifier = source == SubscriptionSource.Stripe ? "sub_paid" : "play-token";
        user.RecordDeletionStoppedRenewal(source, identifier);
        user.CancelDeactivation();
        if (source == SubscriptionSource.Stripe)
            user.SetStripeSubscription(identifier, DateTime.UtcNow.AddDays(20));
        else
            user.SetPlaySubscription(identifier, DateTime.UtcNow.AddDays(20));

        user.StripeRenewalStoppedByDeletion.Should().Be(source == SubscriptionSource.Stripe);
        user.PlayRenewalStoppedByDeletion.Should().Be(source == SubscriptionSource.GooglePlay);
        if (source == SubscriptionSource.Stripe)
        {
            user.RecordStripeRenewalRestored(identifier);
            user.StripeRenewalStoppedByDeletion.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(SubscriptionSource.Stripe, false)]
    [InlineData(SubscriptionSource.Stripe, true)]
    [InlineData(SubscriptionSource.GooglePlay, false)]
    [InlineData(SubscriptionSource.GooglePlay, true)]
    public void SubscriptionReplacementOrRemoval_ClearsCancellationOwnership(SubscriptionSource source, bool remove)
    {
        var user = CreatePaidUser();
        user.RecordDeletionStoppedRenewal(source, source == SubscriptionSource.Stripe ? "sub_paid" : "play-token");
        if (source == SubscriptionSource.Stripe)
        {
            if (remove)
                user.CancelStripeSubscription(SubscriptionLapseReason.Canceled);
            else
                user.SetStripeSubscription("sub_new", DateTime.UtcNow.AddDays(20));
        }
        else if (remove)
            user.CancelPlaySubscription(SubscriptionLapseReason.Canceled);
        else
            user.LinkPlayPurchaseToken("new-token");

        user.StripeRenewalStoppedByDeletion.Should().BeFalse();
        user.PlayRenewalStoppedByDeletion.Should().BeFalse();
    }

    [Theory]
    [InlineData(SubscriptionSource.Stripe, "sub_paid", false)]
    [InlineData(SubscriptionSource.Stripe, "other", true)]
    [InlineData(SubscriptionSource.GooglePlay, "play-token", false)]
    [InlineData(SubscriptionSource.GooglePlay, "other", true)]
    public void RecordDeletionStoppedRenewal_RejectsActiveAccountOrUnlinkedSubscription(
        SubscriptionSource source, string identifier, bool deactivated)
    {
        var user = CreatePaidUser();
        if (!deactivated)
            user.CancelDeactivation();

        var record = () => user.RecordDeletionStoppedRenewal(source, identifier);

        record.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CancellationOwnership_RejectsBlankIdentifiers(string? identifier)
    {
        var user = CreatePaidUser();

        var record = () => user.RecordDeletionStoppedRenewal(SubscriptionSource.Stripe, identifier!);
        var restore = () => user.RecordStripeRenewalRestored(identifier!);

        record.Should().Throw<ArgumentException>();
        restore.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(false, false, "sub_paid")]
    [InlineData(true, true, "sub_paid")]
    [InlineData(true, false, "other")]
    public void StripeRestoration_RejectsIndependentCancellationOrWrongAccountState(bool deletionOwned, bool deactivated,
        string identifier)
    {
        var user = CreatePaidUser();
        if (deletionOwned)
            user.RecordDeletionStoppedRenewal(SubscriptionSource.Stripe, "sub_paid");
        if (!deactivated)
            user.CancelDeactivation();

        var restore = () => user.RecordStripeRenewalRestored(identifier);

        restore.Should().Throw<InvalidOperationException>();
    }

    private static User CreatePaidUser()
    {
        var user = User.Create("Paid", "paid@example.com").Value;
        user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        user.LinkPlayPurchaseToken("play-token");
        user.Deactivate(DateTime.UtcNow.AddDays(27));
        return user;
    }
}
