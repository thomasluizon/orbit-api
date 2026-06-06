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
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyName_HasError()
    {
        var command = ValidCommand() with { Name = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameOver50Chars_HasError()
    {
        var command = ValidCommand() with { Name = new string('a', 51) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_InvalidColor_HasError()
    {
        var command = ValidCommand() with { Color = "red" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Fact]
    public void Validate_EmptyColor_HasError()
    {
        var command = ValidCommand() with { Color = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Fact]
    public void Validate_ColorWithoutHash_HasError()
    {
        var command = ValidCommand() with { Color = "FF5733" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyTagId_HasError()
    {
        var command = ValidCommand() with { TagId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.TagId);
    }
}
