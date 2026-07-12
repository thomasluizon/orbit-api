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

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void Validate_SupportedLanguage_NoError(string language)
    {
        var result = _validator.TestValidate(ValidCommand() with { Language = language });
        result.ShouldNotHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_OverLengthLanguage_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Language = new string('x', 11) });
        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Language = "fr" });
        result.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Validate_EmptyLanguage_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Language = "" });
        result.ShouldHaveValidationErrorFor(x => x.Language);
    }
}
