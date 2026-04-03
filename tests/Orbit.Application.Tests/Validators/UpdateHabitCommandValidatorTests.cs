using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public class UpdateHabitCommandValidatorTests
{
    private readonly UpdateHabitCommandValidator _validator = new();

    private static UpdateHabitCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        HabitId: Guid.NewGuid(),
        Title: "Updated Habit",
        Description: null,
        FrequencyUnit: FrequencyUnit.Day,
        FrequencyQuantity: 1);

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        // Arrange
        var command = ValidCommand() with { UserId = Guid.Empty };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyHabitId_HasError()
    {
        // Arrange
        var command = ValidCommand() with { HabitId = Guid.Empty };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.HabitId);
    }

    [Fact]
    public void Validate_EmptyTitle_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Title = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_TitleOver200Chars_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Title = new string('a', 201) };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_ZeroFrequencyQty_HasError()
    {
        // Arrange
        var command = ValidCommand() with { FrequencyQuantity = 0 };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Fact]
    public void Validate_DaysWithQtyNot1_HasError()
    {
        // Arrange
        var command = ValidCommand() with
        {
            FrequencyQuantity = 2,
            Options = new UpdateHabitCommandOptions(Days: new[] { DayOfWeek.Monday })
        };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }
}
