using FluentValidation.TestHelper;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class GoogleAuthCommandValidatorTests
{
    private readonly GoogleAuthCommandValidator _validator = new();

    private static GoogleAuthCommand ValidCommand() => new(
        AccessToken: "ya29.valid-google-access-token");

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyAccessToken_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { AccessToken = "" });
        result.ShouldHaveValidationErrorFor(x => x.AccessToken);
    }

    [Fact]
    public void Validate_NullAccessToken_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { AccessToken = null! });
        result.ShouldHaveValidationErrorFor(x => x.AccessToken);
    }

    [Fact]
    public void Validate_WhitespaceAccessToken_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { AccessToken = "   " });
        result.ShouldHaveValidationErrorFor(x => x.AccessToken);
    }
}
