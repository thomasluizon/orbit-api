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
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyEmail_HasError()
    {
        var command = ValidCommand() with { Email = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_EmptyCode_HasError()
    {
        var command = ValidCommand() with { Code = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Validate_ShortCode_HasError()
    {
        var command = ValidCommand() with { Code = "123" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Validate_NonNumericCode_HasError()
    {
        var command = ValidCommand() with { Code = "abcdef" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Code);
    }
}
