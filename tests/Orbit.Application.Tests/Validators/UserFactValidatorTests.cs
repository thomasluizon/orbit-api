using FluentValidation.TestHelper;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Validators;

namespace Orbit.Application.Tests.Validators;

public class CreateUserFactCommandValidatorTests
{
    private readonly CreateUserFactCommandValidator _validator = new();

    private static CreateUserFactCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        FactText: "I prefer morning workouts",
        Category: "preference");

    [Fact]
    public void Create_Valid_NoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Create_EmptyText_HasError()
    {
        // Arrange
        var command = ValidCommand() with { FactText = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FactText);
    }

    [Fact]
    public void Create_TextOver500Chars_HasError()
    {
        // Arrange
        var command = ValidCommand() with { FactText = new string('f', 501) };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FactText);
    }

    [Fact]
    public void Create_InvalidCategory_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Category = "invalid" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Category);
    }

    [Theory]
    [InlineData("preference")]
    [InlineData("routine")]
    [InlineData("context")]
    public void Create_ValidCategories_NoErrors(string category)
    {
        // Arrange
        var command = ValidCommand() with { Category = category };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.Category);
    }

    [Fact]
    public void Create_NullCategory_NoError()
    {
        // Arrange
        var command = ValidCommand() with { Category = null };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.Category);
    }
}

public class UpdateUserFactCommandValidatorTests
{
    private readonly UpdateUserFactCommandValidator _validator = new();

    private static UpdateUserFactCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        FactId: Guid.NewGuid(),
        FactText: "Updated fact text",
        Category: "routine");

    [Fact]
    public void Update_Valid_NoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Update_EmptyFactId_HasError()
    {
        // Arrange
        var command = ValidCommand() with { FactId = Guid.Empty };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FactId);
    }

    [Fact]
    public void Update_InvalidCategory_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Category = "invalid" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Category);
    }
}
