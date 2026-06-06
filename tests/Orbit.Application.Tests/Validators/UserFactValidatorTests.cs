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
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Create_EmptyText_HasError()
    {
        var command = ValidCommand() with { FactText = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.FactText);
    }

    [Fact]
    public void Create_TextOver500Chars_HasError()
    {
        var command = ValidCommand() with { FactText = new string('f', 501) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.FactText);
    }

    [Fact]
    public void Create_InvalidCategory_HasError()
    {
        var command = ValidCommand() with { Category = "invalid" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Category);
    }

    [Theory]
    [InlineData("preference")]
    [InlineData("routine")]
    [InlineData("context")]
    public void Create_ValidCategories_NoErrors(string category)
    {
        var command = ValidCommand() with { Category = category };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Category);
    }

    [Fact]
    public void Create_NullCategory_NoError()
    {
        var command = ValidCommand() with { Category = null };

        var result = _validator.TestValidate(command);

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
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Update_EmptyFactId_HasError()
    {
        var command = ValidCommand() with { FactId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.FactId);
    }

    [Fact]
    public void Update_InvalidCategory_HasError()
    {
        var command = ValidCommand() with { Category = "invalid" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Category);
    }
}
