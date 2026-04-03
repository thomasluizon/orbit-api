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
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_PtBrLanguage_NoErrors()
    {
        // Arrange
        var command = ValidCommand() with { Language = "pt-BR" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyLanguage_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Language = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Language = "fr" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_TooLongLanguage_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Language = new string('a', 11) };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Language);
    }
}
