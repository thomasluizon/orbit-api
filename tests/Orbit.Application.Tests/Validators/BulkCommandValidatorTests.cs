using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public class BulkCreateHabitsCommandValidatorTests
{
    private readonly BulkCreateHabitsCommandValidator _validator = new();

    private static BulkCreateHabitsCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Habits: new[]
        {
            new BulkHabitItem("Habit 1", null, FrequencyUnit.Day, 1),
            new BulkHabitItem("Habit 2", null, FrequencyUnit.Week, 1)
        });

    [Fact]
    public void BulkCreate_EmptyList_HasError()
    {
        var command = ValidCommand() with { Habits = Array.Empty<BulkHabitItem>() };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Habits);
    }

    [Fact]
    public void BulkCreate_Over100Items_HasError()
    {
        var habits = Enumerable.Range(0, 101)
            .Select(i => new BulkHabitItem($"Habit {i}", null, FrequencyUnit.Day, 1))
            .ToList();
        var command = ValidCommand() with { Habits = habits };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Habits);
    }

    [Fact]
    public void BulkCreate_ExactlyMaxItems_NoBulkSizeError()
    {
        var habits = Enumerable.Range(0, AppConstants.MaxBulkOperationSize)
            .Select(i => new BulkHabitItem($"Habit {i}", null, FrequencyUnit.Day, 1))
            .ToList();
        var command = ValidCommand() with { Habits = habits };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void BulkCreate_OverMax_ErrorMessageStatesConfiguredMax()
    {
        var habits = Enumerable.Range(0, AppConstants.MaxBulkOperationSize + 1)
            .Select(i => new BulkHabitItem($"Habit {i}", null, FrequencyUnit.Day, 1))
            .ToList();
        var command = ValidCommand() with { Habits = habits };

        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e =>
            e.ErrorMessage == $"Cannot create more than {AppConstants.MaxBulkOperationSize} habits at once");
    }

    [Fact]
    public void BulkCreate_ValidList_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }
}

public class BulkDeleteHabitsCommandValidatorTests
{
    private readonly BulkDeleteHabitsCommandValidator _validator = new();

    private static BulkDeleteHabitsCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        HabitIds: new[] { Guid.NewGuid(), Guid.NewGuid() });

    [Fact]
    public void BulkDelete_EmptyList_HasError()
    {
        var command = ValidCommand() with { HabitIds = Array.Empty<Guid>() };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.HabitIds);
    }

    [Fact]
    public void BulkDelete_Over100Items_HasError()
    {
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();
        var command = ValidCommand() with { HabitIds = ids };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.HabitIds);
    }

    [Fact]
    public void BulkDelete_EmptyHabitId_HasError()
    {
        var command = ValidCommand() with { HabitIds = new[] { Guid.Empty } };

        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Habit ID must not be empty"));
    }

    [Fact]
    public void BulkDelete_ExactlyMaxItems_NoBulkSizeError()
    {
        var ids = Enumerable.Range(0, AppConstants.MaxBulkOperationSize).Select(_ => Guid.NewGuid()).ToList();
        var command = ValidCommand() with { HabitIds = ids };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void BulkDelete_OverMax_ErrorMessageStatesConfiguredMax()
    {
        var ids = Enumerable.Range(0, AppConstants.MaxBulkOperationSize + 1).Select(_ => Guid.NewGuid()).ToList();
        var command = ValidCommand() with { HabitIds = ids };

        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e =>
            e.ErrorMessage == $"Cannot delete more than {AppConstants.MaxBulkOperationSize} habits at once");
    }

    [Fact]
    public void BulkDelete_ValidList_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
