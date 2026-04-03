using FluentValidation.TestHelper;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class SendCodeCommandValidatorTests
{
    private readonly SendCodeCommandValidator _validator = new();

    private static SendCodeCommand ValidCommand() => new(
        Email: "user@example.com");

    [Fact]
    public void Validate_ValidEmail_NoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyEmail_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Email = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_InvalidEmailFormat_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Email = "not-an-email" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }
}
