using FluentAssertions;
using Orbit.Application.Auth.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public class OwedSubscriptionCancellationValidatorTests
{
    [Theory]
    [InlineData(SubscriptionSource.Stripe, "sub_paid", true)]
    [InlineData(SubscriptionSource.GooglePlay, "token", true)]
    [InlineData((SubscriptionSource)99, "token", false)]
    [InlineData(SubscriptionSource.Stripe, "", false)]
    [InlineData(SubscriptionSource.GooglePlay, " ", false)]
    [InlineData(SubscriptionSource.Stripe, null, false)]
    public void Validate_RequiresSupportedProviderAndIdentifier(SubscriptionSource provider, string? identifier, bool valid)
    {
        var result = new OwedSubscriptionCancellationValidator().Validate(new OwedSubscriptionCancellationRequest(provider, identifier!));
        result.IsValid.Should().Be(valid);
    }
}
