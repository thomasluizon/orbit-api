using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public sealed class ConversationOfferTests
{
    [Fact]
    public void DismissalIsIdempotentAndBoundToConversationAndOwner()
    {
        var owner = Guid.NewGuid();
        var conversation = Guid.NewGuid();
        var offer = ConversationOffer.Create(owner, conversation, "reminder:habit").Value;
        offer.Dismiss();
        offer.Dismiss();
        offer.Dismissed.Should().BeTrue();
        offer.UserId.Should().Be(owner);
        offer.ConversationId.Should().Be(conversation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nkey")]
    public void RejectsInvalidKeys(string key) =>
        ConversationOffer.Create(Guid.NewGuid(), Guid.NewGuid(), key).IsFailure.Should().BeTrue();

    [Fact]
    public void RequiresOwnerAndConversation()
    {
        ConversationOffer.Create(Guid.Empty, Guid.NewGuid(), "key").IsFailure.Should().BeTrue();
        ConversationOffer.Create(Guid.NewGuid(), Guid.Empty, "key").IsFailure.Should().BeTrue();
        ConversationOffer.Create(Guid.NewGuid(), Guid.NewGuid(), new string('x', 201)).IsFailure.Should().BeTrue();
    }
}
