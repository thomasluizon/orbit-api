using FluentAssertions;
using Orbit.Application.Marketing.Validators;

namespace Orbit.Application.Tests.Validators;

public class MarketingRecipientDeliveryValidatorTests
{
    [Fact]
    public void DurableJob_RequiresItsBroadcastRecipientAndAddress()
    {
        var validator = new MarketingRecipientDeliveryValidator();
        validator.Validate(new MarketingRecipientDelivery(Guid.NewGuid(), Guid.NewGuid(), "recipient@example.com", "en")).IsValid.Should().BeTrue();
        validator.Validate(new MarketingRecipientDelivery(Guid.Empty, Guid.Empty, "invalid", "")).Errors.Select(error => error.PropertyName)
            .Should().BeEquivalentTo("BroadcastId", "RecipientId", "Email", "Language");
    }
}
