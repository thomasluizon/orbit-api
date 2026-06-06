using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class LogHabitCommandValidatorTests
{
    private readonly LogHabitCommandValidator _validator = new();

    private static LogHabitCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        HabitId: Guid.NewGuid());

    [Fact]
    public void Validate_Valid_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyHabitId_HasError()
    {
        var command = ValidCommand() with { HabitId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.HabitId);
    }

    [Fact]
    public void Validate_WithOptionalDate_NoErrors()
    {
        var command = ValidCommand() with { Date = new DateOnly(2026, 4, 16) };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
