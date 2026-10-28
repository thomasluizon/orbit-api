using FluentAssertions;
using Orbit.Application.Chat.Validators;

namespace Orbit.Application.Tests.Validators;

public sealed class StartingStepArgumentsValidatorTests
{
    [Theory]
    [InlineData("Study", "Open book", "en", 1, true)]
    [InlineData("Estudar", "Abrir o livro", "pt-BR", 1, true)]
    [InlineData(null, "Open", "en", 1, false)]
    [InlineData("Study", null, "en", 1, false)]
    [InlineData("Study", " ", "en", 1, false)]
    [InlineData("Study", "Open\nRead", "en", 1, false)]
    [InlineData("Study", "Open", null, 1, false)]
    [InlineData("Study", "Open", "es", 1, false)]
    [InlineData("Study", "Open", "en", 0, false)]
    [InlineData("Study", "Open", "en", 2, false)]
    public void Validate_RequiresOneBoundedStepInSupportedLanguage(string? goal, string? step, string? language, int count, bool valid)
    {
        new StartingStepArgumentsValidator().Validate(new StartingStepArguments(goal, step, language, count)).IsValid.Should().Be(valid);
    }
}
