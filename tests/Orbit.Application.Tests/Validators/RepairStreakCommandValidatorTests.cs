using FluentValidation.TestHelper;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Validators;

namespace Orbit.Application.Tests.Validators;

public class RepairStreakCommandValidatorTests
{
    private readonly RepairStreakCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidUserId_HasNoErrors()
    {
        var result = _validator.TestValidate(new RepairStreakCommand(Guid.NewGuid()));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(new RepairStreakCommand(Guid.Empty));

        result.ShouldHaveValidationErrorFor(command => command.UserId);
    }
}
