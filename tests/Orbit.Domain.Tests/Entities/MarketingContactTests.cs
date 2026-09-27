using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class MarketingContactTests
{
    [Fact]
    public void ConfirmWaitlist_NormalizesAndGuardsFields()
    {
        var contact = MarketingContact.ConfirmWaitlist(" Person@Example.com ", "pt-BR");

        contact.Email.Should().Be("person@example.com");
        contact.Language.Should().Be("pt-BR");
        contact.Source.Should().Be("waitlist");
        contact.ConfirmedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        FluentActions.Invoking(() => MarketingContact.ConfirmWaitlist("invalid", "en"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => MarketingContact.ConfirmWaitlist("person@example.com", "fr"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnsubscribeAndSuppress_AreIdempotent()
    {
        var contact = MarketingContact.ConfirmWaitlist("person@example.com", "en");
        contact.Unsubscribe();
        contact.Suppress();
        var unsubscribedAt = contact.UnsubscribedAtUtc;
        var suppressedAt = contact.SuppressedAtUtc;

        contact.Unsubscribe();
        contact.Suppress();

        contact.UnsubscribedAtUtc.Should().Be(unsubscribedAt);
        contact.SuppressedAtUtc.Should().Be(suppressedAt);
    }
}
