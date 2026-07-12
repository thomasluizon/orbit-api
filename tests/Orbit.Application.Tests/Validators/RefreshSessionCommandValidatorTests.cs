using System.Security.Cryptography;
using FluentAssertions;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class RefreshSessionCommandValidatorTests
{
    private readonly RefreshSessionCommandValidator _validator = new();

    private static string ValidToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(64));

    [Fact]
    public void Validate_ServerIssuedToken_Passes()
    {
        var result = _validator.Validate(new RefreshSessionCommand(ValidToken()));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Validate_EmptyRefreshToken_Fails(string? token)
    {
        var result = _validator.Validate(new RefreshSessionCommand(token!));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(failure => failure.PropertyName == "RefreshToken");
    }

    [Fact]
    public void Validate_WrongLengthToken_Fails()
    {
        var tooShort = new string('A', RefreshTokenRules.TokenLength - 1);
        var tooLong = new string('A', RefreshTokenRules.TokenLength + 1);

        _validator.Validate(new RefreshSessionCommand(tooShort)).IsValid.Should().BeFalse();
        _validator.Validate(new RefreshSessionCommand(tooLong)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_NonHexToken_Fails()
    {
        var nonHex = new string('Z', RefreshTokenRules.TokenLength);
        var result = _validator.Validate(new RefreshSessionCommand(nonHex));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_LowercaseHexToken_Fails()
    {
        var lowercase = ValidToken().ToLowerInvariant();
        var result = _validator.Validate(new RefreshSessionCommand(lowercase));
        result.IsValid.Should().BeFalse();
    }
}
