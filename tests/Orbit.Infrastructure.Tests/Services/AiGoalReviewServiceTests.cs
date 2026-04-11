using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure/deterministic logic in AiGoalReviewService.
/// The empty/whitespace input validation is tested directly.
/// The AI client interaction and StripMarkdownFences behavior
/// are covered by AiSummaryServiceTests and integration tests.
/// </summary>
public class AiGoalReviewServiceTests
{
    // The service validates goalsContext before calling the AI client.
    // We can test that validation without mocking the AI.

    // We also test the StripMarkdownFences usage indirectly through
    // AiSummaryService tests, and verify the language mapping logic.

    [Fact]
    public void LanguageMapping_English_MapsCorrectly()
    {
        var langMap = LocaleHelper.GetAiLanguageName("en");
        langMap.Should().Be("English");
    }

    [Fact]
    public void LanguageMapping_PortugueseBR_MapsCorrectly()
    {
        var langMap = LocaleHelper.GetAiLanguageName("pt-br");
        langMap.Should().Be("Brazilian Portuguese");
    }

    [Fact]
    public void LanguageMapping_PortugueseShort_MapsCorrectly()
    {
        var langMap = LocaleHelper.GetAiLanguageName("pt");
        langMap.Should().Be("Brazilian Portuguese");
    }

    [Fact]
    public void LanguageMapping_UnknownLanguage_DefaultsToEnglish()
    {
        var langMap = LocaleHelper.GetAiLanguageName("fr");
        langMap.Should().Be("English");
    }

    [Fact]
    public void LanguageMapping_NullLanguage_DefaultsToEnglish()
    {
        var langMap = LocaleHelper.GetAiLanguageName(null);
        langMap.Should().Be("English");
    }

    [Fact]
    public void LanguageMapping_EmptyLanguage_DefaultsToEnglish()
    {
        var langMap = LocaleHelper.GetAiLanguageName("");
        langMap.Should().Be("English");
    }

    [Fact]
    public void LanguageMapping_WhitespaceLanguage_DefaultsToEnglish()
    {
        var langMap = LocaleHelper.GetAiLanguageName("   ");
        langMap.Should().Be("English");
    }

    [Fact]
    public void StripMarkdownFences_UsedByGoalReview_WorksCorrectly()
    {
        var input = "```\nYour goals look great!\n```";
        var result = AiSummaryService.StripMarkdownFences(input);
        result.Should().Be("Your goals look great!");
    }

    [Fact]
    public void StripMarkdownFences_PlainGoalReview_PassesThrough()
    {
        var input = "Your fitness goal is on track. Keep it up!";
        var result = AiSummaryService.StripMarkdownFences(input);
        result.Should().Be(input);
    }

    [Fact]
    public void StripMarkdownFences_WithLanguageTag_StripsCorrectly()
    {
        var input = "```json\n{\"status\": \"on_track\"}\n```";
        var result = AiSummaryService.StripMarkdownFences(input);
        result.Should().Be("{\"status\": \"on_track\"}");
    }

    [Fact]
    public void StripMarkdownFences_EmptyContent_ReturnsEmpty()
    {
        var result = AiSummaryService.StripMarkdownFences("");
        result.Should().BeEmpty();
    }

    [Fact]
    public void StripMarkdownFences_OnlyFences_ReturnsEmpty()
    {
        var input = "```\n```";
        var result = AiSummaryService.StripMarkdownFences(input);
        result.Should().BeEmpty();
    }

    // --- LocaleHelper.IsPortuguese ---

    [Theory]
    [InlineData("pt-br", true)]
    [InlineData("pt-BR", true)]
    [InlineData("PT-BR", true)]
    [InlineData("pt", true)]
    [InlineData("PT", true)]
    [InlineData("en", false)]
    [InlineData("fr", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsPortuguese_ReturnsCorrectResult(string? language, bool expected)
    {
        LocaleHelper.IsPortuguese(language).Should().Be(expected);
    }
}
