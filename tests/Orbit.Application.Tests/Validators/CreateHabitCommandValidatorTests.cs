using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public class CreateHabitCommandValidatorTests
{
    private readonly CreateHabitCommandValidator _validator = new();

    private static CreateHabitCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Title: "My Habit",
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
    public void Validate_NegativeFrequencyQty_HasError()
    {
        // Arrange
        var command = ValidCommand() with { FrequencyQuantity = -1 };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Fact]
    public void Validate_NullFrequencyQty_NoError()
    {
        // Arrange
        var command = ValidCommand() with { FrequencyQuantity = null };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Fact]
    public void Validate_DaysWithQtyNot1_HasError()
    {
        // Arrange
        var command = ValidCommand() with
        {
            FrequencyQuantity = 2,
            Days = new[] { DayOfWeek.Monday }
        };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Days);
    }

    [Fact]
    public void Validate_DaysWithQty1_NoError()
    {
        // Arrange
        var command = ValidCommand() with
        {
            FrequencyQuantity = 1,
            Days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday }
        };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.Days);
    }

    [Fact]
    public void Validate_Over20SubHabits_HasError()
    {
        // Arrange
        var subHabits = Enumerable.Range(0, 21).Select(i => $"Sub {i}").ToList();
        var command = ValidCommand() with { SubHabits = subHabits };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.SubHabits);
    }

    [Fact]
    public void Validate_SubHabitEmptyTitle_HasError()
    {
        // Arrange
        var command = ValidCommand() with { SubHabits = new[] { "" } };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Sub-habit title must not be empty"));
    }

    [Fact]
    public void Validate_SubHabitTitleOver200Chars_HasError()
    {
        // Arrange
        var command = ValidCommand() with { SubHabits = new[] { new string('x', 201) } };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Sub-habit title must not exceed 200 characters"));
    }
}
