using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class DeleteHabitCommandValidatorTests
{
    private readonly DeleteHabitCommandValidator _validator = new();

    private static DeleteHabitCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        HabitId: Guid.NewGuid());

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
    public void Validate_EmptyHabitId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { HabitId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.HabitId);
    }
}
