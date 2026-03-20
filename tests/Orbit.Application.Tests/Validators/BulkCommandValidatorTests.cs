using FluentAssertions;
using FluentValidation.TestHelper;
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
        // Arrange
        var command = ValidCommand() with { Habits = Array.Empty<BulkHabitItem>() };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Habits);
    }

    [Fact]
    public void BulkCreate_Over100Items_HasError()
    {
        // Arrange
        var habits = Enumerable.Range(0, 101)
            .Select(i => new BulkHabitItem($"Habit {i}", null, FrequencyUnit.Day, 1))
            .ToList();
        var command = ValidCommand() with { Habits = habits };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Habits);
    }

    [Fact]
    public void BulkCreate_ValidList_NoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
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
        // Arrange
        var command = ValidCommand() with { HabitIds = Array.Empty<Guid>() };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.HabitIds);
    }

    [Fact]
    public void BulkDelete_Over100Items_HasError()
    {
        // Arrange
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList();
        var command = ValidCommand() with { HabitIds = ids };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.HabitIds);
    }

    [Fact]
    public void BulkDelete_EmptyHabitId_HasError()
    {
        // Arrange
        var command = ValidCommand() with { HabitIds = new[] { Guid.Empty } };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Habit ID must not be empty"));
    }

    [Fact]
    public void BulkDelete_ValidList_NoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }
}
