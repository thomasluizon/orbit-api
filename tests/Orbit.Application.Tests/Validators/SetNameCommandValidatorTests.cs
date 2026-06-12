using FluentValidation.TestHelper;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;

namespace Orbit.Application.Tests.Validators;

public class SetNameCommandValidatorTests
{
    private readonly SetNameCommandValidator _validator = new();

    private static SetNameCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Name: "Ana Clara");

    [Fact]
    public void Validate_ValidName_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_AccentedName_NoErrors()
    {
        var command = ValidCommand() with { Name = "João Sebastião" };

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
    public void Validate_WhitespaceName_HasError()
    {
        var command = ValidCommand() with { Name = "   " };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_TooLongName_HasError()
    {
        var command = ValidCommand() with { Name = new string('a', 51) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_MaxLengthAfterTrim_NoErrors()
    {
        var command = ValidCommand() with { Name = $"  {new string('a', 50)}  " };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
