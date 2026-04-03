using FluentValidation.TestHelper;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Validators;

namespace Orbit.Application.Tests.Validators;

public class ActivateStreakFreezeCommandValidatorTests
{
    private readonly ActivateStreakFreezeCommandValidator _validator = new();

    private static ActivateStreakFreezeCommand ValidCommand() => new(
        UserId: Guid.NewGuid());

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { UserId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
