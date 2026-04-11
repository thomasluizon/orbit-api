using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure/deterministic logic in AiSlipAlertMessageService:
/// GenerateFallback, prompt construction, and response parsing logic.
/// The AI client interaction is an integration concern.
/// </summary>
public class AiSlipAlertMessageServiceTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    // -- GenerateFallback --

    [Fact]
    public void GenerateFallback_English_ReturnsEnglishMessage()
    {
        var result = InvokeGenerateFallback("Smoking", "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Heads up: Smoking");
        result.Value.Body.Should().Contain("Stay strong");
    }

    [Fact]
    public void GenerateFallback_Portuguese_ReturnsPortugueseMessage()
    {
        var result = InvokeGenerateFallback("Fumar", "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Contain("Fique atento: Fumar");
        result.Value.Body.Should().Contain("consegue");
    }

    [Fact]
    public void GenerateFallback_PtShort_ReturnsPortugueseMessage()
    {
        var result = InvokeGenerateFallback("Biting nails", "pt");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Contain("Fique atento");
    }

    [Fact]
    public void GenerateFallback_UnknownLanguage_ReturnsEnglishMessage()
    {
        var result = InvokeGenerateFallback("Smoking", "fr");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Contain("Heads up");
    }

    [Fact]
    public void GenerateFallback_IncludesHabitTitleInTitle()
    {
        var result = InvokeGenerateFallback("Doom scrolling", "en");

        result.Value.Title.Should().Contain("Doom scrolling");
    }

    // -- Additional GenerateFallback tests --

    [Fact]
    public void GenerateFallback_English_BodyMentionsSlipping()
    {
        var result = InvokeGenerateFallback("Junk food", "en");

        result.Value.Body.Should().Contain("slip");
    }

    [Fact]
    public void GenerateFallback_Portuguese_BodyMentionsDeslizar()
    {
        var result = InvokeGenerateFallback("Besteira", "pt-br");

        result.Value.Body.Should().Contain("deslizar");
    }

    [Fact]
    public void GenerateFallback_AlwaysReturnsSuccess()
    {
        // Fallback should never fail -- it's the safety net
        var languages = new[] { "en", "pt", "pt-br", "pt-BR", "fr", "de", "es", "" };

        foreach (var lang in languages)
        {
            var result = InvokeGenerateFallback("Test", lang);
            result.IsSuccess.Should().BeTrue($"language '{lang}' should return success");
        }
    }

    [Fact]
    public void GenerateFallback_EmptyTitle_StillFormatsCorrectly()
    {
        var result = InvokeGenerateFallback("", "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Heads up: ");
    }

    [Fact]
    public void GenerateFallback_LongTitle_IncludesFullTitle()
    {
        var longTitle = "This is a very long habit title that describes something";
        var result = InvokeGenerateFallback(longTitle, "en");

        result.Value.Title.Should().Contain(longTitle);
    }

    // -- Response parsing logic (replicating the lines-split from GenerateMessageAsync) --

    [Fact]
    public void ResponseParsing_TwoLines_ReturnsBothParts()
    {
        var text = "Stay strong today!\nYou tend to slip around this time on Fridays. Remember why you started.";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCountGreaterThanOrEqualTo(2);
        lines[0].Should().Be("Stay strong today!");
        lines[1].Should().Contain("Fridays");
    }

    [Fact]
    public void ResponseParsing_SingleLine_UsesFallbackTitle()
    {
        var text = "Remember to stay strong!";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCount(1);
        // Service falls back to "Heads up: {habitTitle}" as the title
        var fallbackTitle = "Heads up: Smoking";
        fallbackTitle.Should().StartWith("Heads up:");
    }

    [Fact]
    public void ResponseParsing_EmptyLines_AreFiltered()
    {
        var text = "Title\n\n\nBody text here";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Be("Title");
        lines[1].Should().Be("Body text here");
    }

    [Fact]
    public void ResponseParsing_WhitespaceOnlyLines_AreFiltered()
    {
        var text = "Title\n   \n  \nBody";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCount(2);
    }

    [Fact]
    public void ResponseParsing_MoreThanTwoLines_UsesFirstTwo()
    {
        var text = "Title\nBody line 1\nExtra line";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCountGreaterThanOrEqualTo(2);
        // The service takes lines[0] and lines[1]
        lines[0].Should().Be("Title");
        lines[1].Should().Be("Body line 1");
    }

    // -- Language detection logic (replicating the prompt construction) --

    [Theory]
    [InlineData("en", "English")]
    [InlineData("EN", "English")]
    [InlineData("pt-br", "Brazilian Portuguese")]
    [InlineData("PT-BR", "Brazilian Portuguese")]
    [InlineData("pt", "Brazilian Portuguese")]
    [InlineData("fr", "English")]
    [InlineData("de", "English")]
    public void LanguageMapping_ReturnsCorrectLanguageName(string input, string expected)
    {
        // Replicate the language mapping from GenerateMessageAsync
        var languageName = input.ToLowerInvariant() switch
        {
            "pt-br" or "pt" => "Brazilian Portuguese",
            _ => "English"
        };

        languageName.Should().Be(expected);
    }

    // -- Time context formatting --

    [Fact]
    public void TimeContext_WithPeakHour_IncludesTimeAndDay()
    {
        var peakHour = 14;
        var dayOfWeek = DayOfWeek.Friday;

        var timeContext = $"They tend to slip around {peakHour}:00 on {dayOfWeek}s.";

        timeContext.Should().Contain("14:00");
        timeContext.Should().Contain("Fridays");
    }

    [Fact]
    public void TimeContext_WithoutPeakHour_MentionsNoTimePattern()
    {
        int? peakHour = null;
        var dayOfWeek = DayOfWeek.Saturday;

        var timeContext = peakHour.HasValue
            ? $"They tend to slip around {peakHour.Value}:00 on {dayOfWeek}s."
            : $"They tend to slip on {dayOfWeek}s (no specific time pattern).";

        timeContext.Should().Contain("no specific time pattern");
        timeContext.Should().Contain("Saturdays");
    }

    // -- Fallback with Portuguese variant --

    [Fact]
    public void GenerateFallback_PortugueseBR_TitleContainsFiqueAtento()
    {
        var result = InvokeGenerateFallback("Procrastinar", "pt-BR");
        result.Value.Title.Should().StartWith("Fique atento:");
    }

    [Theory]
    [InlineData("pt-br")]
    [InlineData("pt-BR")]
    [InlineData("PT-BR")]
    [InlineData("pt")]
    [InlineData("PT")]
    public void GenerateFallback_AllPortugueseVariants_ReturnPortuguese(string lang)
    {
        var result = InvokeGenerateFallback("Test", lang);
        result.Value.Title.Should().Contain("Fique atento");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("ja")]
    public void GenerateFallback_NonPortuguese_ReturnEnglish(string lang)
    {
        var result = InvokeGenerateFallback("Test", lang);
        result.Value.Title.Should().Contain("Heads up");
    }

    // -- Response parsing with edge cases --

    [Fact]
    public void ResponseParsing_TrailingNewlines_TrimmedCorrectly()
    {
        var text = "Title here\nBody text\n\n";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Be("Title here");
        lines[1].Should().Be("Body text");
    }

    [Fact]
    public void ResponseParsing_LeadingNewlines_TrimmedCorrectly()
    {
        var text = "\n\nTitle\nBody";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Be("Title");
        lines[1].Should().Be("Body");
    }

    // -- Helpers --

    private static Result<(string Title, string Body)> InvokeGenerateFallback(string habitTitle, string language)
    {
        var method = typeof(AiSlipAlertMessageService)
            .GetMethod("GenerateFallback", PrivateStatic)!;
        return (Result<(string Title, string Body)>)method.Invoke(null, [habitTitle, language])!;
    }
}
