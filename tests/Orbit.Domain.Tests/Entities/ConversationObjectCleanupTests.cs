using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public sealed class ConversationObjectCleanupTests
{
    [Fact]
    public void Create_RetainsTheAccountAndConversationPrefix()
    {
        var userId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var cleanup = ConversationObjectCleanup.Create(userId, conversationId);
        cleanup.UserId.Should().Be(userId);
        cleanup.ConversationId.Should().Be(conversationId);
    }

    [Fact]
    public void Create_RejectsEmptyPrefixIdentifiers()
    {
        var emptyUser = () => ConversationObjectCleanup.Create(Guid.Empty, Guid.NewGuid());
        emptyUser.Should().Throw<ArgumentException>();
        var emptyConversation = () => ConversationObjectCleanup.Create(Guid.NewGuid(), Guid.Empty);
        emptyConversation.Should().Throw<ArgumentException>();
    }
}
