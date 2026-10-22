using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class ConversationTurnTests
{
    [Theory]
    [InlineData("assistant", "{\"parts\":[]}", true)]
    [InlineData("assistant", "[]", false)]
    [InlineData("assistant", "invalid", false)]
    [InlineData("user", "{}", false)]
    public void Create_ResponseRequiresAssistantAndJsonObject(string role, string json, bool accepted)
    {
        var result = ConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), role, DateTime.UtcNow, null, "hello", "[]", json);
        result.IsSuccess.Should().Be(accepted);
        if (accepted) result.Value.ResponseJson.Should().Be(json);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("assistant")]
    public void Create_SupportedRoles_StoresMetadata(string role)
    {
        var userId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var createdAtUtc = DateTime.UtcNow;
        var result = ConversationTurn.Create(userId, turnId, role, createdAtUtc, "model", "content", "[]");
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(userId);
        result.Value.TurnId.Should().Be(turnId);
        result.Value.CreatedAtUtc.Should().Be(createdAtUtc);
        result.Value.Role.Should().Be(role);
    }

    [Theory]
    [InlineData("system", "hello", "[]")]
    [InlineData("user", "", "[]")]
    [InlineData("assistant", "hello", "{}")]
    [InlineData("assistant", "hello", "invalid")]
    public void Create_InvalidData_IsRejected(string role, string content, string tools) =>
        ConversationTurn.Create(Guid.NewGuid(), Guid.NewGuid(), role, DateTime.UtcNow, null, content, tools)
            .IsFailure.Should().BeTrue();

    [Fact]
    public void Create_InvalidIdentifiersTimestampAndLengths_AreRejected()
    {
        var user = Guid.NewGuid();
        var turn = Guid.NewGuid();
        ConversationTurn.Create(Guid.Empty, turn, "user", DateTime.UtcNow, null, "hello", "[]").IsFailure.Should().BeTrue();
        ConversationTurn.Create(user, Guid.Empty, "user", DateTime.UtcNow, null, "hello", "[]").IsFailure.Should().BeTrue();
        ConversationTurn.Create(user, turn, "user", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified), null, "hello", "[]").IsFailure.Should().BeTrue();
        ConversationTurn.Create(user, turn, "user", DateTime.UtcNow, new string('m', 201), "hello", "[]").IsFailure.Should().BeTrue();
        ConversationTurn.Create(user, turn, "user", DateTime.UtcNow, null, new string('c', ConversationTurn.MaxTextLength + 1), "[]").IsFailure.Should().BeTrue();
    }
}
