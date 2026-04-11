using FluentAssertions;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class RefreshSessionCommandValidatorTests
{
    private readonly RefreshSessionCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidRefreshToken_Passes()
    {
        var result = _validator.Validate(new RefreshSessionCommand("valid-token"));
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
        result.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("RefreshToken");
    }
}
