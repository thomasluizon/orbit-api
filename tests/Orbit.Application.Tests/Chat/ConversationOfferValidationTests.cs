using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class ConversationOfferValidationTests
{
    [Fact]
    public async Task DismissalCommandKeepsTheAuthenticatedOwnerAndStableConversation()
    {
        var store = Substitute.For<IConversationOfferStore>();
        var request = new DismissConversationOffersCommand(Guid.NewGuid(), Guid.NewGuid(), ["reminder:habit"]);
        (await new DismissConversationOffersCommandValidator().ValidateAsync(request)).IsValid.Should().BeTrue();
        (await new DismissConversationOffersCommandHandler(store).Handle(request, default)).IsSuccess.Should().BeTrue();
        await store.Received(1).DismissAsync(request.UserId, request.ConversationId, request.OfferKeys, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void RejectsUnboundedDismissalRequests(int count) =>
        new DismissConversationOffersCommandValidator().Validate(new DismissConversationOffersCommand(Guid.NewGuid(), Guid.NewGuid(),
            Enumerable.Repeat("offer", count).ToArray())).IsValid.Should().BeFalse();

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OffersRequireBothRenderersAndTheStableConversation(bool parts, bool identity)
    {
        var client = new AgentClientContext(SupportsParts: parts, SupportsPendingOperationChanges: true,
            SupportsProactiveOffers: true, ConversationId: identity ? Guid.NewGuid() : Guid.Empty);
        new ProcessUserChatCommandValidator().Validate(new ProcessUserChatCommand(Guid.NewGuid(), "Read records", ClientContext: client)).IsValid.Should().BeFalse();
    }
}
