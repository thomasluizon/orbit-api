using FluentValidation.TestHelper;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Validators;

namespace Orbit.Application.Tests.Validators;

public class CreateTagCommandValidatorTests
{
    private readonly CreateTagCommandValidator _validator = new();

    private static CreateTagCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Name: "Health",
        Color: "#FF5733");

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { UserId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyName_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = "" });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NullName_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = null! });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameOver50Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = new string('a', 51) });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameExactly50Chars_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = new string('a', 50) });
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_EmptyColor_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Color = "" });
        result.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Fact]
    public void Validate_NullColor_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Color = null! });
        result.ShouldHaveValidationErrorFor(x => x.Color);
    }

    [Theory]
    [InlineData("#ff5733")]
    [InlineData("#AABBCC")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    public void Validate_ValidHexColors_NoErrors(string color)
    {
        var result = _validator.TestValidate(ValidCommand() with { Color = color });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("red")]
    [InlineData("FF5733")]
    [InlineData("#GGG000")]
    [InlineData("#FFF")]
    [InlineData("#FF57331")]
    [InlineData("rgb(255,0,0)")]
    public void Validate_InvalidHexColors_HasError(string color)
    {
        var result = _validator.TestValidate(ValidCommand() with { Color = color });
        result.ShouldHaveValidationErrorFor(x => x.Color);
    }
}
