using FluentAssertions;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class GoogleCodeAuthCommandValidatorTests
{
    private readonly GoogleCodeAuthCommandValidator _validator = new();

    [Theory]
    [InlineData("", "verifier", "https://app.test/callback")]
    [InlineData("code", "", "https://app.test/callback")]
    [InlineData("code", "verifier", "")]
    public void MissingRequiredField_IsInvalid(string code, string verifier, string redirectUri)
    {
        _validator.Validate(new GoogleCodeAuthCommand(code, verifier, redirectUri)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void SupportedLanguage_IsValid()
    {
        _validator.Validate(new GoogleCodeAuthCommand("code", "verifier", "https://app.test/callback", "pt-BR"))
            .IsValid.Should().BeTrue();
    }
}
