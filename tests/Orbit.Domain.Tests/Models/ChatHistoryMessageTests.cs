using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public class ChatHistoryMessageTests
{
    [Theory]
    [InlineData("assistant")]
    [InlineData("Assistant")]
    [InlineData("ai")]
    [InlineData("AI")]
    public void NormalizeRole_AssistantAliases_ReturnsAssistant(string role)
    {
        var normalized = ChatHistoryMessage.NormalizeRole(role);

        normalized.Should().Be(ChatHistoryMessage.AssistantRole);
    }
}
