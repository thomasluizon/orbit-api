using System.Text.RegularExpressions;
using FluentAssertions;

namespace Orbit.Application.Tests.Notifications;

internal static partial class SentenceCaseAssertions
{
    public static void AssertTitle(string title)
    {
        var sentenceStart = true;
        var previousEnd = 0;
        foreach (Match match in Words().Matches(title))
        {
            sentenceStart |= title[previousEnd..match.Index].IndexOfAny(['.', '!', '?']) >= 0;
            var word = match.Value;
            var expected = word is "Orbit" or "Astra" or "Wrapped" or "Google" or "Calendar"
                ? word
                : sentenceStart
                    ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
                    : word.ToLowerInvariant();
            word.Should().Be(expected, "notification title '{0}' must use sentence case", title);
            sentenceStart = false;
            previousEnd = match.Index + match.Length;
        }
    }

    [GeneratedRegex(@"\p{L}+(?:'\p{L}+)?")]
    private static partial Regex Words();
}

public class SentenceCaseAssertionsTests
{
    [Theory]
    [InlineData("Conquista desbloqueada: mês perfeito")]
    [InlineData("Achievement unlocked: perfect month")]
    [InlineData("You reached level 2")]
    [InlineData("Your Wrapped is ready")]
    [InlineData("Google Calendar disconnected")]
    [InlineData("Orbit test")]
    [InlineData("Astra is here")]
    public void AssertTitle_SentenceCaseWithProperNames_Passes(string title)
    {
        SentenceCaseAssertions.AssertTitle(title);
    }

    [Theory]
    [InlineData("Perfect Month")]
    [InlineData("Mês Perfeito")]
    [InlineData("Achievement Unlocked: Perfect Month")]
    [InlineData("Conquista desbloqueada: Mês perfeito")]
    [InlineData("Half-Year Hero")]
    public void AssertTitle_TitleCase_Throws(string title)
    {
        var act = () => SentenceCaseAssertions.AssertTitle(title);

        act.Should().Throw<Xunit.Sdk.XunitException>();
    }
}
