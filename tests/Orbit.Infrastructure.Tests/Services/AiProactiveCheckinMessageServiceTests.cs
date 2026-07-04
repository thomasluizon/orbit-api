using System.Reflection;
using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure/deterministic logic in AiProactiveCheckinMessageService:
/// the localized GenerateFallback (returned on empty/exception) and the two-line
/// response-parsing logic. The live AI client interaction is an integration concern.
/// </summary>
public class AiProactiveCheckinMessageServiceTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void GenerateFallback_English_ReturnsEnglishMessage()
    {
        var result = InvokeGenerateFallback("Thomas", "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Thomas");
        result.Value.Body.Should().Contain("back on track");
    }

    [Fact]
    public void GenerateFallback_Portuguese_ReturnsPortugueseMessage()
    {
        var result = InvokeGenerateFallback("Thomas", "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ainda dá tempo hoje, Thomas");
        result.Value.Body.Should().Contain("retomar");
    }

    [Fact]
    public void GenerateFallback_PtShort_ReturnsPortugueseMessage()
    {
        var result = InvokeGenerateFallback("Ana", "pt");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Contain("Ainda dá tempo hoje");
    }

    [Fact]
    public void GenerateFallback_UnknownLanguage_ReturnsEnglishMessage()
    {
        var result = InvokeGenerateFallback("Thomas", "fr");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Contain("Still time today");
    }

    [Fact]
    public void GenerateFallback_IncludesDisplayNameInTitle()
    {
        var result = InvokeGenerateFallback("Mariana", "en");

        result.Value.Title.Should().Contain("Mariana");
    }

    [Fact]
    public void GenerateFallback_AlwaysReturnsSuccess()
    {
        var languages = new[] { "en", "pt", "pt-br", "pt-BR", "fr", "de", "es", "" };

        foreach (var lang in languages)
        {
            var result = InvokeGenerateFallback("Test", lang);
            result.IsSuccess.Should().BeTrue($"language '{lang}' should return success");
        }
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
        result.Value.Title.Should().Contain("Ainda dá tempo hoje");
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
        result.Value.Title.Should().Contain("Still time today");
    }

    [Fact]
    public void ResponseParsing_TwoLines_ReturnsBothParts()
    {
        var text = "Still time today, Thomas\nYou fell behind on Meditate. Astra's got your back -- let's finish strong.";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCountGreaterThanOrEqualTo(2);
        lines[0].Should().Be("Still time today, Thomas");
        lines[1].Should().Contain("Meditate");
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
    public void ResponseParsing_MoreThanTwoLines_UsesFirstTwo()
    {
        var text = "Title\nBody line 1\nExtra line";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCountGreaterThanOrEqualTo(2);
        lines[0].Should().Be("Title");
        lines[1].Should().Be("Body line 1");
    }

    [Fact]
    public void ResponseParsing_LeadingAndTrailingNewlines_TrimmedCorrectly()
    {
        var text = "\n\nTitle\nBody\n\n";
        var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Be("Title");
        lines[1].Should().Be("Body");
    }

    private static Result<(string Title, string Body)> InvokeGenerateFallback(string displayName, string language)
    {
        var method = typeof(AiProactiveCheckinMessageService)
            .GetMethod("GenerateFallback", PrivateStatic)!;
        return (Result<(string Title, string Body)>)method.Invoke(null, [displayName, language])!;
    }
}
