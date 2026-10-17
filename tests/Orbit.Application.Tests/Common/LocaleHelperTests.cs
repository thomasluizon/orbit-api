using FluentAssertions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class LocaleHelperTests
{
    [Theory]
    [InlineData(null, "English")]
    [InlineData("", "English")]
    [InlineData("  ", "English")]
    [InlineData("en", "English")]
    [InlineData("en-US", "English")]
    [InlineData("fr", "English")]
    [InlineData("pt-BR", "Brazilian Portuguese")]
    [InlineData("pt-br", "Brazilian Portuguese")]
    [InlineData("pt", "Brazilian Portuguese")]
    [InlineData("PT-BR", "Brazilian Portuguese")]
    public void GetAiLanguageName_ReturnsCorrectLanguage(string? input, string expected)
    {
        LocaleHelper.GetAiLanguageName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("en", false)]
    [InlineData("en-US", false)]
    [InlineData("fr", false)]
    [InlineData("pt-BR", true)]
    [InlineData("pt-br", true)]
    [InlineData("pt", true)]
    [InlineData("PT-BR", true)]
    [InlineData("PT", true)]
    public void IsPortuguese_ReturnsCorrectResult(string? input, bool expected)
    {
        LocaleHelper.IsPortuguese(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("pt-BR", true)]
    [InlineData("en, pt-BR", false)]
    [InlineData("pt-BR, en", true)]
    [InlineData("pt;q=0.1,en;q=0.9", false)]
    [InlineData("en;q=0.1,pt;q=0.9", true)]
    [InlineData("pt;q=0,en;q=0.5", false)]
    [InlineData("en;q=0,pt;q=0.5", true)]
    [InlineData("pt;q=invalid,en;q=0.5", true)]
    [InlineData("pt;level=1;q=0.2,en;q=0.8", false)]
    public void IsPortugueseAcceptLanguage_UsesHighestQualityThenHeaderOrder(
        string? input, bool expected)
    {
        LocaleHelper.IsPortugueseAcceptLanguage(input).Should().Be(expected);
    }
}
