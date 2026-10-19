using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class OwedSubscriptionCancellationTests
{
    [Theory]
    [InlineData(SubscriptionSource.Stripe, "sub_owed")]
    [InlineData(SubscriptionSource.GooglePlay, "purchase-token")]
    public void Create_PreservesOnlyBillingIdentifierAndInitialState(SubscriptionSource provider, string identifier)
    {
        var record = OwedSubscriptionCancellation.Create(provider, identifier);
        record.Provider.Should().Be(provider);
        record.ProviderIdentifier.Should().Be(identifier);
        record.Attempts.Should().Be(0);
        record.LastAttemptAtUtc.Should().BeNull();
        record.ExhaustedAtUtc.Should().BeNull();
        record.NextAttemptAtUtc.Should().Be(record.CreatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_RejectsMissingIdentifier(string? identifier)
    {
        var create = () => OwedSubscriptionCancellation.Create(SubscriptionSource.Stripe, identifier!);
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsUnknownProvider()
    {
        var create = () => OwedSubscriptionCancellation.Create((SubscriptionSource)99, "identifier");
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RecordAttempt_BacksOffAndContinuesDailyAfterExhaustion()
    {
        var record = OwedSubscriptionCancellation.Create(SubscriptionSource.Stripe, "sub_owed");
        for (var attempt = 1; attempt <= OwedSubscriptionCancellation.MaxAttempts; attempt++)
        {
            var attemptedAtUtc = record.NextAttemptAtUtc;
            record.RecordAttempt(attemptedAtUtc);
            record.Attempts.Should().Be(attempt);
            record.LastAttemptAtUtc.Should().Be(attemptedAtUtc);
            if (attempt < OwedSubscriptionCancellation.MaxAttempts)
                record.NextAttemptAtUtc.Should().Be(attemptedAtUtc.AddSeconds(attempt switch { 1 => 60, 2 => 300, _ => 3600 }));
        }
        record.ExhaustedAtUtc.Should().NotBeNull();
        record.ProviderIdentifier.Should().Be("sub_owed");
        var exhaustedAtUtc = record.ExhaustedAtUtc;
        var retryAtUtc = record.NextAttemptAtUtc;
        record.RecordAttempt(retryAtUtc);
        record.Attempts.Should().Be(OwedSubscriptionCancellation.MaxAttempts + 1);
        record.ExhaustedAtUtc.Should().Be(exhaustedAtUtc);
        record.NextAttemptAtUtc.Should().Be(retryAtUtc.AddDays(1));
        record.RecordFailure(retryAtUtc.AddMinutes(2));
        record.NextAttemptAtUtc.Should().Be(retryAtUtc.AddDays(1).AddMinutes(2));
    }

    [Fact]
    public void StateChanges_RejectEarlyOrNonUtcAttemptsAndUnclaimedFailures()
    {
        var record = OwedSubscriptionCancellation.Create(SubscriptionSource.Stripe, "sub_owed");
        var early = () => record.RecordAttempt(record.NextAttemptAtUtc.AddSeconds(-1));
        early.Should().Throw<ArgumentOutOfRangeException>();
        var nonUtc = () => record.RecordAttempt(DateTime.SpecifyKind(record.NextAttemptAtUtc, DateTimeKind.Unspecified));
        nonUtc.Should().Throw<ArgumentOutOfRangeException>();
        var unclaimed = () => record.RecordFailure(record.NextAttemptAtUtc);
        unclaimed.Should().Throw<InvalidOperationException>();
        var attemptedAtUtc = record.NextAttemptAtUtc;
        record.RecordAttempt(attemptedAtUtc);
        var prematureFailure = () => record.RecordFailure(attemptedAtUtc.AddTicks(-1));
        prematureFailure.Should().Throw<ArgumentOutOfRangeException>();
        var nonUtcFailure = () => record.RecordFailure(DateTime.SpecifyKind(attemptedAtUtc, DateTimeKind.Local));
        nonUtcFailure.Should().Throw<ArgumentOutOfRangeException>();
    }
}
