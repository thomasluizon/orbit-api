using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class ReorderHabitsCommandValidatorTests
{
    private readonly ReorderHabitsCommandValidator _validator = new();

    private static ReorderHabitsCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Positions: [new HabitPositionUpdate(Guid.NewGuid(), 0)]);

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
    public void Validate_EmptyPositions_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Positions = [] });
        result.ShouldHaveValidationErrorFor(x => x.Positions);
    }

    [Fact]
    public void Validate_DuplicateHabitIds_HasError()
    {
        var habitId = Guid.NewGuid();
        var command = ValidCommand() with
        {
            Positions = [
                new HabitPositionUpdate(habitId, 0),
                new HabitPositionUpdate(habitId, 1)
            ]
        };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Positions);
    }

    [Fact]
    public void Validate_UniqueHabitIds_NoError()
    {
        var command = ValidCommand() with
        {
            Positions = [
                new HabitPositionUpdate(Guid.NewGuid(), 0),
                new HabitPositionUpdate(Guid.NewGuid(), 1)
            ]
        };
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.Positions);
    }

    [Fact]
    public void Validate_EmptyHabitIdInPosition_HasError()
    {
        var command = ValidCommand() with
        {
            Positions = [new HabitPositionUpdate(Guid.Empty, 0)]
        };
        var result = _validator.TestValidate(command);
        result.Errors.Should().Contain(e => e.PropertyName.Contains("HabitId"));
    }

    [Fact]
    public void Validate_NegativePosition_HasError()
    {
        var command = ValidCommand() with
        {
            Positions = [new HabitPositionUpdate(Guid.NewGuid(), -1)]
        };
        var result = _validator.TestValidate(command);
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Position"));
    }
}
