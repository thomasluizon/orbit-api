using FluentAssertions;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class FollowUpContextTests
{
    [Theory]
    [InlineData("Show my habits?", true)]
    [InlineData("Open http://example.test", false)]
    [InlineData("[[orbit:unknown]]", false)]
    public void Validator_RejectsUnsafeSuggestionContent(string question, bool accepted) =>
        new ChatFollowUpValidator().Validate(new ChatFollowUp("Habits", question, target: "habits"))
            .IsValid.Should().Be(accepted);
}
