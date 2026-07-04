using FluentValidation.TestHelper;
using Orbit.Application.Waitlist.Commands;
using Orbit.Application.Waitlist.Validators;

namespace Orbit.Application.Tests.Validators;

public class JoinWaitlistCommandValidatorTests
{
    private readonly JoinWaitlistCommandValidator _validator = new();

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void Validate_ValidEmailAndLanguage_NoErrors(string language)
    {
        var result = _validator.TestValidate(new JoinWaitlistCommand("user@example.com", language));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyEmail_HasError()
    {
        var result = _validator.TestValidate(new JoinWaitlistCommand("", "en"));

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_InvalidEmailFormat_HasError()
    {
        var result = _validator.TestValidate(new JoinWaitlistCommand("not-an-email", "en"));

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void Validate_UnsupportedLanguage_HasError()
    {
        var result = _validator.TestValidate(new JoinWaitlistCommand("user@example.com", "fr"));

        result.ShouldHaveValidationErrorFor(x => x.Language);
    }
}
