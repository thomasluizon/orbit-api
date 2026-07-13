using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public class UpdateHabitCommandValidatorTests
{
    private readonly UpdateHabitCommandValidator _validator = new();

    private static readonly DayOfWeek[] MondayOnly = new[] { DayOfWeek.Monday };
    private static readonly int[] DuplicateReminderTimes = new[] { 15, 15 };
    private static readonly int[] OutOfRangeReminderTimes = new[] { -5 };

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
    public void Validate_EmptyTitle_HasError()
    {
        var command = ValidCommand() with { Title = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_TitleOver200Chars_HasError()
    {
        var command = ValidCommand() with { Title = new string('a', 201) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void Validate_ZeroFrequencyQty_HasError()
    {
        var command = ValidCommand() with { FrequencyQuantity = 0 };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Fact]
    public void Validate_DaysWithQtyNot1_HasError()
    {
        var command = ValidCommand() with
        {
            FrequencyQuantity = 2,
            Options = new UpdateHabitCommandOptions(Days: MondayOnly)
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public void Validate_DaysWithNonDayUnit_HasError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = FrequencyUnit.Week,
            FrequencyQuantity = 1,
            Options = new UpdateHabitCommandOptions(Days: MondayOnly)
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public void Validate_DaysWithDayUnitQty1_NoError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = FrequencyUnit.Day,
            FrequencyQuantity = 1,
            Options = new UpdateHabitCommandOptions(Days: MondayOnly)
        };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public void Validate_OneTimeTaskWithEndDate_HasError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = null,
            FrequencyQuantity = null,
            IsGeneral = false,
            Options = new UpdateHabitCommandOptions(EndDate: new DateOnly(2030, 1, 1))
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.EndDate : null);
    }

    [Fact]
    public void Validate_RecurringTaskWithEndDate_NoError()
    {
        var command = ValidCommand() with
        {
            IsGeneral = false,
            Options = new UpdateHabitCommandOptions(EndDate: new DateOnly(2030, 1, 1))
        };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.EndDate : null);
    }

    [Fact]
    public void Validate_ReminderTimes_Duplicates_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new UpdateHabitCommandOptions(ReminderTimes: DuplicateReminderTimes)
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Options!.ReminderTimes);
    }

    [Fact]
    public void Validate_ReminderTimes_OutOfRange_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new UpdateHabitCommandOptions(ReminderTimes: OutOfRangeReminderTimes)
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Options!.ReminderTimes);
    }
}
