using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public sealed class ChatConversationTurnTests
{
    [Theory]
    [InlineData("{invalid}")]
    [InlineData("[]")]
    [InlineData("")]
    public void InvalidStorageData_IsRejected(string json) => ChatConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), DateTime.UtcNow, json, false, false).IsFailure.Should().BeTrue();

    [Fact]
    public void IdentityAndUtcTimestamp_AreRequired()
    {
        ChatConversationTurn.Create(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, "{}", false, false)
            .IsFailure.Should().BeTrue();
        ChatConversationTurn.Create(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), DateTime.UtcNow, "{}", false, false)
            .IsFailure.Should().BeTrue();
        ChatConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, DateTime.UtcNow, "{}", false, false)
            .IsFailure.Should().BeTrue();
        ChatConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.SpecifyKind(new(2026, 11, 1), DateTimeKind.Unspecified), "{}", false, false)
            .IsFailure.Should().BeTrue();
        ChatConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, "{}", true, true)
            .IsSuccess.Should().BeTrue();
    }
}
