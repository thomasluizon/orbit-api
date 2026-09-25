using FluentAssertions;
using Orbit.Application.Chat;

namespace Orbit.Application.Tests.Chat;

public sealed class FollowUpDirectiveTests
{
    [Fact]
    public void Extract_FourValidLines_KeepsThree()
    {
        var (text, items) = FollowUpDirective.Extract(
            "Ready.\n[[orbit:followups]]\nWhat changed?\nShow my habits?\nWhat is next?\nAnything else?");

        text.Should().Be("Ready.");
        items.Should().Equal("What changed?", "Show my habits?", "What is next?");
    }

    [Fact]
    public void Extract_OneValidLine_ReturnsNoItems()
    {
        var (_, items) = FollowUpDirective.Extract("Ready.\n[[orbit:followups]]\nWhat changed?");

        items.Should().BeNull();
    }

    [Fact]
    public void Extract_DropsUrlAndDuplicate_ThenKeepsPortugueseQuestions()
    {
        var (_, items) = FollowUpDirective.Extract(
            "Pronto.\n[[orbit:followups]]\nVeja http://example.com\nComo foi meu dia?\nComo foi meu dia?\nQuais hábitos faltam?");

        items.Should().Equal("Como foi meu dia?", "Quais hábitos faltam?");
    }
}
