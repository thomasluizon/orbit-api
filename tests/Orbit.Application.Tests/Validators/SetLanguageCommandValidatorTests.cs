using FluentValidation.TestHelper;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;

namespace Orbit.Application.Tests.Validators;

public class SetLanguageCommandValidatorTests
{
    private readonly SetLanguageCommandValidator _validator = new();

    private static SetLanguageCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Language: "en");

    [Fact]
    public void Validate_ValidLanguage_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_PtBrLanguage_NoErrors()
    {
        var command = ValidCommand() with { Language = "pt-BR" };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyLanguage_HasError()
    {
        var command = ValidCommand() with { Language = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        var command = ValidCommand() with { Language = "fr" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_TooLongLanguage_HasError()
    {
        var command = ValidCommand() with { Language = new string('a', 11) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }
}
