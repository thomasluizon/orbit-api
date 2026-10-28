using FluentAssertions;
using Orbit.Domain.Models;

namespace Orbit.Domain.Tests.Models;

public sealed class StartingStepTests
{
    [Theory]
    [InlineData("", "Open book", "en")]
    [InlineData("Study", "", "en")]
    [InlineData("Study", "Open\nRead", "en")]
    [InlineData("Study", "Open", "es")]
    public void Constructor_RejectsInvalidStep(string goal, string step, string language)
    {
        var create = () => new StartingStep(goal, step, language);
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_RejectsOversizedTexts()
    {
        var goal = () => new StartingStep(new string('g', 201), "Open", "en");
        var step = () => new StartingStep("Study", new string('s', 201), "en");
        goal.Should().Throw<ArgumentException>();
        step.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void Constructor_PreservesLanguageAndTrimsText(string language)
    {
        var result = new StartingStep(" Study ", " Open book ", language);
        result.Goal.Should().Be("Study");
        result.Step.Should().Be("Open book");
        result.Language.Should().Be(language);
    }
}
