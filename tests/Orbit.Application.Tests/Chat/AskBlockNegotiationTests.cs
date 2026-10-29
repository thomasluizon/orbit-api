using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class AskBlockNegotiationTests
{
    private static readonly AgentClientContext ChoiceClient = new(SupportsParts: true, PartKinds: ["choice", "question"]);

    private static AstraBlockDefinition SingleChoice(string prompt, string title = "Walk details") =>
        new(AstraBlockKinds.Question, title, [], [],
            Questions: [new AstraQuestion("walk", "singleChoice", prompt, [new("morning", "Morning walk"), new("evening", "Evening walk")])]);

    [Fact]
    public void Definition_UsesTheModelQuestionBeforeTheFieldPrompt()
    {
        var negotiated = AskBlockNegotiation.Definition(SingleChoice("Pick one"), ChoiceClient,
            JsonSerializer.SerializeToElement(new { question = "Which Walk would you like to skip?" }));

        negotiated.Form.Should().Be(AstraBlockKinds.Choice);
        negotiated.Title.Should().Be("Which Walk would you like to skip?");
        negotiated.Options.Select(option => option.Id).Should().Equal("morning", "evening");
    }

    [Theory]
    [InlineData("""{"question":"   "}""")]
    [InlineData("""{"question":7}""")]
    [InlineData("""{"title":"Walk details"}""")]
    [InlineData("""["question"]""")]
    public void Definition_FallsBackToTheFieldPromptWithoutAUsableModelQuestion(string arguments)
    {
        var negotiated = AskBlockNegotiation.Definition(SingleChoice("Which Walk?"), ChoiceClient,
            JsonDocument.Parse(arguments).RootElement.Clone());

        negotiated.Form.Should().Be(AstraBlockKinds.Choice);
        negotiated.Title.Should().Be("Which Walk?");
    }

    [Fact]
    public void Definition_FallsBackToTheHeadingWhenTheFieldPromptIsBlank()
    {
        var negotiated = AskBlockNegotiation.Definition(SingleChoice(" "), ChoiceClient, default);

        negotiated.Form.Should().Be(AstraBlockKinds.Choice);
        negotiated.Title.Should().Be("Walk details");
    }

    [Theory]
    [InlineData(121)]
    [InlineData(0)]
    public void Definition_KeepsTheQuestionFormWhenNoTitleFitsAChoice(int headingLength)
    {
        var definition = SingleChoice(" ", new string('a', headingLength));

        AskBlockNegotiation.Definition(definition, ChoiceClient, default).Should().BeSameAs(definition);
    }
}
