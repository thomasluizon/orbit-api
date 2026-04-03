using FluentValidation.TestHelper;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class VerifyCodeCommandValidatorTests
{
    private readonly VerifyCodeCommandValidator _validator = new();

    private static VerifyCodeCommand ValidCommand() => new(
        Email: "user@example.com",
        Code: "123456");

    [Fact]
    public void Validate_ValidInput_NoErrors()
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
    public void Validate_EmptyCode_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Code = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Validate_ShortCode_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Code = "123" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Validate_NonNumericCode_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Code = "abcdef" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Code);
    }
}
