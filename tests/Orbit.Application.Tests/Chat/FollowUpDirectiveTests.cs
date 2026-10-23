using FluentAssertions;
using Orbit.Application.Chat;

namespace Orbit.Application.Tests.Chat;

public sealed class FollowUpDirectiveTests
{
    [Fact]
    public void Extract_FourValidLines_KeepsTwo()
    {
        var (text, items) = FollowUpDirective.Extract(
            "Ready.\n[[orbit:followups]]\nWhat changed?\nShow my habits?\nWhat is next?\nAnything else?");

        text.Should().Be("Ready.");
        items.Should().Equal("What changed?", "Show my habits?");
    }

    [Fact]
    public void Extract_OneValidLine_ReturnsOneItem()
    {
        var (_, items) = FollowUpDirective.Extract("Ready.\n[[orbit:followups]]\nWhat changed?");

        items.Should().Equal("What changed?");
    }

    [Fact]
    public void Extract_DropsUrlAndDuplicate_ThenKeepsPortugueseQuestions()
    {
        var (_, items) = FollowUpDirective.Extract(
            "Pronto.\n[[orbit:followups]]\nVeja http://example.com\nComo foi meu dia?\nComo foi meu dia?\nQuais hábitos faltam?");

        items.Should().Equal("Como foi meu dia?", "Quais hábitos faltam?");
    }
    [Theory]
    [InlineData("Plain text", "text:Plain text")]
    [InlineData(" A \n[[orbit:metrics]]\n B ", "text:A|directive:[[orbit:metrics]]|text:B")]
    [InlineData("[[orbit:day]]A[[orbit:metrics]] B [[orbit:goals]]C",
        "directive:[[orbit:day]]|text:A|directive:[[orbit:metrics]]|text:B|directive:[[orbit:goals]]|text:C")]
    [InlineData(" \n[[orbit:metrics]] \n ", "directive:[[orbit:metrics]]")]
    [InlineData("A[[orbit:followups]]\nNext?\nThen?\n[[orbit:day]]\n[[orbit:metrics]]",
        "text:A|directive:[[orbit:day]]|directive:[[orbit:metrics]]")]
    public void Parse_PreservesOrderedTextAndDirectives(string response, string expected)
    {
        var parsed = FollowUpDirective.Parse(response);

        string.Join('|', parsed.Segments.Select(segment => segment.Directive is { } directive
            ? $"directive:{directive}" : $"text:{segment.Text}")).Should().Be(expected);
    }

    [Fact]
    public void Process_SplitDirective_PreservesPositionWithoutStreamingToken()
    {
        var parser = new ResponseDirectiveParser();
        var first = parser.Process("A\n[[orb");
        var second = parser.Process("it:metrics]]\nB", flush: true);

        (first + second).Should().Be("A\n\nB");
        parser.Complete().Segments.Should().Equal(
            new ResponseDirectiveSegment(Text: "A"),
            new ResponseDirectiveSegment(Directive: "[[orbit:metrics]]"),
            new ResponseDirectiveSegment(Text: "B"));
    }
}
