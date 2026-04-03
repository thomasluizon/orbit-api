using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Queries;
using Orbit.Application.Goals.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public class CreateGoalCommandValidatorTests
{
    private readonly CreateGoalCommandValidator _validator = new();

    private static CreateGoalCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Title: "My Goal",
        Description: null,
        TargetValue: 100,
        Unit: "pages",
        Deadline: null);

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
    public void Validate_EmptyTitle_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Title = "" });
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_TitleOver200Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Title = new string('a', 201) });
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_TitleExactly200Chars_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Title = new string('a', 200) });
        result.ShouldNotHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_ZeroTargetValue_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { TargetValue = 0 });
        result.ShouldHaveValidationErrorFor(x => x.TargetValue);
    }

    [Fact]
    public void Validate_NegativeTargetValue_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { TargetValue = -10 });
        result.ShouldHaveValidationErrorFor(x => x.TargetValue);
    }

    [Fact]
    public void Validate_EmptyUnit_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Unit = "" });
        result.ShouldHaveValidationErrorFor(x => x.Unit);
    }

    [Fact]
    public void Validate_UnitOver50Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Unit = new string('u', 51) });
        result.ShouldHaveValidationErrorFor(x => x.Unit);
    }
}

public class DeleteGoalCommandValidatorTests
{
    private readonly DeleteGoalCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var command = new DeleteGoalCommand(Guid.NewGuid(), Guid.NewGuid());
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(new DeleteGoalCommand(Guid.Empty, Guid.NewGuid()));
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyGoalId_HasError()
    {
        var result = _validator.TestValidate(new DeleteGoalCommand(Guid.NewGuid(), Guid.Empty));
        result.ShouldHaveValidationErrorFor(x => x.GoalId);
    }
}

public class GetGoalsQueryValidatorTests
{
    private readonly GetGoalsQueryValidator _validator = new();

    [Fact]
    public void Validate_ValidQuery_NoErrors()
    {
        var result = _validator.TestValidate(new GetGoalsQuery(Guid.NewGuid()));
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_PageZero_HasError()
    {
        var result = _validator.TestValidate(new GetGoalsQuery(Guid.NewGuid(), Page: 0));
        result.ShouldHaveValidationErrorFor(x => x.Page);
    }

    [Fact]
    public void Validate_NegativePage_HasError()
    {
        var result = _validator.TestValidate(new GetGoalsQuery(Guid.NewGuid(), Page: -1));
        result.ShouldHaveValidationErrorFor(x => x.Page);
    }

    [Fact]
    public void Validate_PageSizeZero_HasError()
    {
        var result = _validator.TestValidate(new GetGoalsQuery(Guid.NewGuid(), PageSize: 0));
        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }

    [Fact]
    public void Validate_PageSizeExceedsMax_HasError()
    {
        var result = _validator.TestValidate(new GetGoalsQuery(Guid.NewGuid(), PageSize: 201));
        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }

    [Fact]
    public void Validate_PageSizeAtMax_NoError()
    {
        var result = _validator.TestValidate(new GetGoalsQuery(Guid.NewGuid(), PageSize: 200));
        result.ShouldNotHaveValidationErrorFor(x => x.PageSize);
    }
}

public class LinkHabitsToGoalCommandValidatorTests
{
    private readonly LinkHabitsToGoalCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var command = new LinkHabitsToGoalCommand(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = new LinkHabitsToGoalCommand(Guid.Empty, Guid.NewGuid(), [Guid.NewGuid()]);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyGoalId_HasError()
    {
        var command = new LinkHabitsToGoalCommand(Guid.NewGuid(), Guid.Empty, [Guid.NewGuid()]);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.GoalId);
    }

    [Fact]
    public void Validate_TooManyHabits_HasError()
    {
        var habitIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToList();
        var command = new LinkHabitsToGoalCommand(Guid.NewGuid(), Guid.NewGuid(), habitIds);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.HabitIds);
    }

    [Fact]
    public void Validate_MaxHabits_NoError()
    {
        var habitIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var command = new LinkHabitsToGoalCommand(Guid.NewGuid(), Guid.NewGuid(), habitIds);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.HabitIds);
    }
}

public class ReorderGoalsCommandValidatorTests
{
    private readonly ReorderGoalsCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var command = new ReorderGoalsCommand(Guid.NewGuid(), [new GoalPositionUpdate(Guid.NewGuid(), 0)]);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = new ReorderGoalsCommand(Guid.Empty, [new GoalPositionUpdate(Guid.NewGuid(), 0)]);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyPositions_HasError()
    {
        var command = new ReorderGoalsCommand(Guid.NewGuid(), []);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Positions);
    }
}

public class UpdateGoalCommandValidatorTests
{
    private readonly UpdateGoalCommandValidator _validator = new();

    private static UpdateGoalCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        GoalId: Guid.NewGuid(),
        Title: "Updated Goal",
        Description: null,
        TargetValue: 100,
        Unit: "pages",
        Deadline: null);

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

    [Fact]
    public void Validate_EmptyTitle_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Title = "" });
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_TitleOver200Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Title = new string('a', 201) });
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_ZeroTargetValue_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { TargetValue = 0 });
        result.ShouldHaveValidationErrorFor(x => x.TargetValue);
    }

    [Fact]
    public void Validate_EmptyUnit_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Unit = "" });
        result.ShouldHaveValidationErrorFor(x => x.Unit);
    }

    [Fact]
    public void Validate_UnitOver50Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Unit = new string('u', 51) });
        result.ShouldHaveValidationErrorFor(x => x.Unit);
    }
}

public class UpdateGoalProgressCommandValidatorTests
{
    private readonly UpdateGoalProgressCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var command = new UpdateGoalProgressCommand(Guid.NewGuid(), Guid.NewGuid(), 50);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = new UpdateGoalProgressCommand(Guid.Empty, Guid.NewGuid(), 50);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyGoalId_HasError()
    {
        var command = new UpdateGoalProgressCommand(Guid.NewGuid(), Guid.Empty, 50);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.GoalId);
    }

    [Fact]
    public void Validate_NegativeNewValue_HasError()
    {
        var command = new UpdateGoalProgressCommand(Guid.NewGuid(), Guid.NewGuid(), -1);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.NewValue);
    }

    [Fact]
    public void Validate_ZeroNewValue_NoError()
    {
        var command = new UpdateGoalProgressCommand(Guid.NewGuid(), Guid.NewGuid(), 0);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.NewValue);
    }
}

public class UpdateGoalStatusCommandValidatorTests
{
    private readonly UpdateGoalStatusCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var command = new UpdateGoalStatusCommand(Guid.NewGuid(), Guid.NewGuid(), GoalStatus.Completed);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = new UpdateGoalStatusCommand(Guid.Empty, Guid.NewGuid(), GoalStatus.Active);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyGoalId_HasError()
    {
        var command = new UpdateGoalStatusCommand(Guid.NewGuid(), Guid.Empty, GoalStatus.Active);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.GoalId);
    }

    [Fact]
    public void Validate_InvalidStatus_HasError()
    {
        var command = new UpdateGoalStatusCommand(Guid.NewGuid(), Guid.NewGuid(), (GoalStatus)999);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.NewStatus);
    }

    [Theory]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Abandoned)]
    public void Validate_ValidStatus_NoError(GoalStatus status)
    {
        var command = new UpdateGoalStatusCommand(Guid.NewGuid(), Guid.NewGuid(), status);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.NewStatus);
    }
}
