using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class LinkGoalsToHabitCommandValidatorTests
{
    private readonly LinkGoalsToHabitCommandValidator _validator = new();

    private static LinkGoalsToHabitCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        HabitId: Guid.NewGuid(),
        GoalIds: [Guid.NewGuid()]);

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

    [Fact]
    public void Validate_TooManyGoals_HasError()
    {
        var goalIds = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList();
        var result = _validator.TestValidate(ValidCommand() with { GoalIds = goalIds });
        result.ShouldHaveValidationErrorFor(x => x.GoalIds);
    }

    [Fact]
    public void Validate_MaxGoals_NoError()
    {
        var goalIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var result = _validator.TestValidate(ValidCommand() with { GoalIds = goalIds });
        result.ShouldNotHaveValidationErrorFor(x => x.GoalIds);
    }

    [Fact]
    public void Validate_EmptyGoalIds_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { GoalIds = [] });
        result.ShouldNotHaveValidationErrorFor(x => x.GoalIds);
    }
}
