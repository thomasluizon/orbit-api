using System.Security.Cryptography;
using FluentAssertions;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class LogoutSessionCommandValidatorTests
{
    private readonly LogoutSessionCommandValidator _validator = new();

    private static string ValidToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(64));

    [Fact]
    public void Validate_ServerIssuedToken_Passes()
    {
        var result = _validator.Validate(new LogoutSessionCommand(ValidToken()));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Validate_EmptyRefreshToken_Fails(string? token)
    {
        var result = _validator.Validate(new LogoutSessionCommand(token!));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(failure => failure.PropertyName == "RefreshToken");
    }

    [Fact]
    public void Validate_MalformedToken_Fails()
    {
        var nonHex = new string('Z', RefreshTokenRules.TokenLength);
        var result = _validator.Validate(new LogoutSessionCommand(nonHex));
        result.IsValid.Should().BeFalse();
    }
}
