using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure/deterministic logic in AiFactExtractionService.
/// The AI client interaction is an integration concern.
/// We test the BuildExtractionPrompt logic to ensure it correctly
/// formats the prompt with user messages and existing facts.
/// </summary>
public class AiFactExtractionServiceTests
{
    [Fact]
    public void BuildExtractionPrompt_NoExistingFacts_IncludesNonePlaceholder()
    {
        var method = typeof(AiFactExtractionService)
            .GetMethod("BuildExtractionPrompt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var prompt = (string)method.Invoke(null, [
            "I work from home",
            "That's great!",
            (IReadOnlyList<Domain.Entities.UserFact>)Array.Empty<Domain.Entities.UserFact>()
        ])!;

        prompt.Should().Contain("I work from home");
        prompt.Should().Contain("That's great!");
        prompt.Should().Contain("(none)");
    }

    [Fact]
    public void BuildExtractionPrompt_NullAiResponse_IncludesPlaceholder()
    {
        var method = typeof(AiFactExtractionService)
            .GetMethod("BuildExtractionPrompt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var prompt = (string)method.Invoke(null, [
            "Hello",
            null!,
            (IReadOnlyList<Domain.Entities.UserFact>)Array.Empty<Domain.Entities.UserFact>()
        ])!;

        prompt.Should().Contain("(no response yet)");
    }

    [Fact]
    public void BuildExtractionPrompt_ContainsExtractionInstructions()
    {
        var method = typeof(AiFactExtractionService)
            .GetMethod("BuildExtractionPrompt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var prompt = (string)method.Invoke(null, [
            "Test message",
            "Test response",
            (IReadOnlyList<Domain.Entities.UserFact>)Array.Empty<Domain.Entities.UserFact>()
        ])!;

        prompt.Should().Contain("Extract Personal Facts");
        prompt.Should().Contain("preference");
        prompt.Should().Contain("routine");
        prompt.Should().Contain("context");
    }

    [Fact]
    public void BuildExtractionPrompt_ContainsUserMessage()
    {
        var method = typeof(AiFactExtractionService)
            .GetMethod("BuildExtractionPrompt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var prompt = (string)method.Invoke(null, [
            "I am a software engineer who works night shifts",
            "Interesting! Night shifts can be tough.",
            (IReadOnlyList<Domain.Entities.UserFact>)Array.Empty<Domain.Entities.UserFact>()
        ])!;

        prompt.Should().Contain("I am a software engineer who works night shifts");
        prompt.Should().Contain("Night shifts can be tough");
    }

    [Fact]
    public void BuildExtractionPrompt_ContainsNegativeExamples()
    {
        var method = typeof(AiFactExtractionService)
            .GetMethod("BuildExtractionPrompt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var prompt = (string)method.Invoke(null, [
            "msg",
            "resp",
            (IReadOnlyList<Domain.Entities.UserFact>)Array.Empty<Domain.Entities.UserFact>()
        ])!;

        // Ensure the prompt contains guidance about what NOT to extract
        prompt.Should().Contain("NEVER extract");
        prompt.Should().Contain("habit intentions");
    }
}
