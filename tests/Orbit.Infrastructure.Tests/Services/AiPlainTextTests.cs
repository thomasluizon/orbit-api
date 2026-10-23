using FluentAssertions;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AiPlainTextTests
{
    [Theory]
    [InlineData("**Water** and *Meals* with __Orbit__.", "Water and Meals with Orbit.")]
    [InlineData("***Water*** and _Meals_.", "Water and Meals.")]
    [InlineData("# Progress\n- **Water**\n1. *Meals*\n+ __Orbit__", "Progress\nWater\nMeals\nOrbit")]
    [InlineData("```text\n**Water**\n```", "Water")]
    [InlineData("Try [Water](https://example.test) and `Meals`.", "Try Water and Meals.")]
    [InlineData("~~Skip~~ Water.", "Skip Water.")]
    [InlineData("Use A*B and habit_name; 2 * 3 = 6.", "Use A*B and habit_name; 2 * 3 = 6.")]
    [InlineData("An unmatched * stays.", "An unmatched * stays.")]
    public void Normalize_RemovesFormattingAndPreservesLiteralPunctuation(string input, string expected)
    {
        AiPlainText.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_PlainNumericNamesDoNotHideListMarkers()
    {
        AiPlainText.Normalize("1. **Water**", ["1", "."]).Should().Be("Water");
    }
}
