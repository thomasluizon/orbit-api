using FluentValidation.TestHelper;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Validators;

namespace Orbit.Application.Tests.Validators;

public class UpdateTagCommandValidatorTests
{
    private readonly UpdateTagCommandValidator _validator = new();

    private static UpdateTagCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        TagId: Guid.NewGuid(),
        Name: "Work",
        Color: "#FF5733");

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
    public void Validate_EmptyName_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Name = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameOver50Chars_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Name = new string('a', 51) };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_InvalidColor_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Color = "red" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Fact]
    public void Validate_EmptyColor_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Color = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Fact]
    public void Validate_ColorWithoutHash_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Color = "FF5733" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        // Arrange
        var command = ValidCommand() with { UserId = Guid.Empty };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyTagId_HasError()
    {
        // Arrange
        var command = ValidCommand() with { TagId = Guid.Empty };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.TagId);
    }
}
