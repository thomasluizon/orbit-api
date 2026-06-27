using FluentValidation.TestHelper;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Validators;

namespace Orbit.Application.Tests.Validators;

public class RestoreGoalCommandValidatorTests
{
    private readonly RestoreGoalCommandValidator _validator = new();

    private static RestoreGoalCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        GoalId: Guid.NewGuid());

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

    [Fact]
    public void Validate_EmptyGoalId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { GoalId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.GoalId);
    }
}
