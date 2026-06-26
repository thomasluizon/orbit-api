using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AiTagSuggestionServiceTests
{
    [Fact]
    public void BuildPrompt_IncludesTitleDescriptionAndExistingTags()
    {
        var prompt = AiTagSuggestionService.BuildPrompt(
            "Morning run",
            "Jog around the park",
            new[] { "Health", "Fitness" },
            "en");

        prompt.Should().Contain("Morning run");
        prompt.Should().Contain("Jog around the park");
        prompt.Should().Contain("Health");
        prompt.Should().Contain("Fitness");
        prompt.Should().Contain("English");
        prompt.Should().Contain("\"tags\"");
    }

    [Fact]
    public void BuildPrompt_NoExistingTags_RendersPlaceholder()
    {
        var prompt = AiTagSuggestionService.BuildPrompt("Read a book", "x", Array.Empty<string>(), "en");

        prompt.Should().Contain("(none yet)");
    }

    [Fact]
    public void BuildPrompt_NullDescription_RendersPlaceholder()
    {
        var prompt = AiTagSuggestionService.BuildPrompt("Read a book", null, new[] { "Learning" }, "en");

        prompt.Should().Contain("(no description)");
    }

    [Fact]
    public void BuildPrompt_PortugueseLanguage_RequestsPortugueseOutput()
    {
        var prompt = AiTagSuggestionService.BuildPrompt("Correr", "Corrida matinal", Array.Empty<string>(), "pt-BR");

        prompt.Should().Contain("Brazilian Portuguese");
    }
}
