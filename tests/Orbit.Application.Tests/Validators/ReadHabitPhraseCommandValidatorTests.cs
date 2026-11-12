using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class ReadHabitPhraseCommandValidatorTests
{
    private readonly ReadHabitPhraseCommandValidator _validator = new();

    private static ReadHabitPhraseCommand ValidCommand() =>
        new(Guid.NewGuid(), "tomar remédio todo dia às 8", "pt-BR");

    [Fact]
    public void Validate_Valid_NoErrors() =>
        _validator.TestValidate(ValidCommand()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Validate_EmptyPhrase_HasError() =>
        _validator.TestValidate(ValidCommand() with { Phrase = "" }).ShouldHaveValidationErrorFor(x => x.Phrase);

    [Fact]
    public void Validate_PhraseOverTheTitleLimit_HasError() =>
        _validator.TestValidate(ValidCommand() with { Phrase = new string('a', 201) }).ShouldHaveValidationErrorFor(x => x.Phrase);

    [Fact]
    public void Validate_UnsupportedLanguage_HasError() =>
        _validator.TestValidate(ValidCommand() with { Language = "xx" }).ShouldHaveValidationErrorFor(x => x.Language);

    [Fact]
    public void Validate_EmptyUser_HasError() =>
        _validator.TestValidate(ValidCommand() with { UserId = Guid.Empty }).ShouldHaveValidationErrorFor(x => x.UserId);
}
