using FluentAssertions;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Validators;

namespace Orbit.Application.Tests.Validators;

public class LogoutAllSessionsCommandValidatorTests
{
    private readonly LogoutAllSessionsCommandValidator _validator = new();

    [Fact]
    public void Validate_WithUserId_Passes()
    {
        var result = _validator.Validate(new LogoutAllSessionsCommand(Guid.NewGuid()));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyUserId_Fails()
    {
        var result = _validator.Validate(new LogoutAllSessionsCommand(Guid.Empty));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be("UserId");
    }
}
