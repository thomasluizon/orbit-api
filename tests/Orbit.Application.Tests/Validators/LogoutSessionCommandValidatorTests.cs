using FluentAssertions;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class LogoutSessionCommandValidatorTests
{
    private readonly LogoutSessionCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidRefreshToken_Passes()
    {
        var result = _validator.Validate(new LogoutSessionCommand("valid-token"));
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
        result.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("RefreshToken");
    }
}
