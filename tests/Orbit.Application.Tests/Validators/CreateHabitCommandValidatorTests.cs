using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

using static Orbit.Domain.Enums.ScheduledReminderWhen;

namespace Orbit.Application.Tests.Validators;

public class CreateHabitCommandValidatorTests
{
    private readonly CreateHabitCommandValidator _validator = new();

    private static readonly DayOfWeek[] MondayOnly = new[] { DayOfWeek.Monday };
    private static readonly DayOfWeek[] MondayAndWednesday = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday };
    private static readonly int[] ValidReminderTimes = new[] { 0, 15, 60, 1440 };
    private static readonly int[] DuplicateReminderTimes = new[] { 15, 15 };
    private static readonly int[] NegativeReminderTimes = new[] { -1 };
    private static readonly int[] OverMaxReminderTimes = new[] { AppConstants.MaxReminderMinutesBefore + 1 };

    private static CreateHabitCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Title: "My Habit",
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
    public void Validate_NegativeFrequencyQty_HasError()
    {
        var command = ValidCommand() with { FrequencyQuantity = -1 };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Fact]
    public void Validate_NullFrequencyQty_NoError()
    {
        var command = ValidCommand() with { FrequencyUnit = null, FrequencyQuantity = null };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Fact]
    public void Validate_DaysWithQtyNot1_HasError()
    {
        var command = ValidCommand() with
        {
            FrequencyQuantity = 2,
            Options = new HabitCommandOptions(Days: MondayOnly)
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public void Validate_DaysWithQty1_NoError()
    {
        var command = ValidCommand() with
        {
            FrequencyQuantity = 1,
            Options = new HabitCommandOptions(Days: MondayAndWednesday)
        };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public void Validate_DaysWithNonDayUnit_HasError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = FrequencyUnit.Week,
            FrequencyQuantity = 1,
            Options = new HabitCommandOptions(Days: MondayOnly)
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public void Validate_DaysWhenFlexible_NoError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = FrequencyUnit.Week,
            FrequencyQuantity = 1,
            Options = new HabitCommandOptions(Days: MondayOnly, IsFlexible: true)
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
            Options = new HabitCommandOptions(EndDate: new DateOnly(2030, 1, 1))
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.EndDate : null);
    }

    [Fact]
    public void Validate_RecurringTaskWithEndDate_NoError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(EndDate: new DateOnly(2030, 1, 1))
        };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.EndDate : null);
    }

    [Fact]
    public void Validate_GeneralHabitWithEndDate_NoError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = null,
            FrequencyQuantity = null,
            IsGeneral = true,
            Options = new HabitCommandOptions(EndDate: new DateOnly(2030, 1, 1))
        };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.EndDate : null);
    }

    [Fact]
    public void Validate_Over20SubHabits_HasError()
    {
        var subHabits = Enumerable.Range(0, 21).Select(i => $"Sub {i}").ToList();
        var command = ValidCommand() with { SubHabits = subHabits };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.SubHabits);
    }

    [Fact]
    public void Validate_SubHabitEmptyTitle_HasError()
    {
        var command = ValidCommand() with { SubHabits = new[] { "" } };

        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Sub-habit title must not be empty"));
    }

    [Fact]
    public void Validate_SubHabitTitleOver200Chars_HasError()
    {
        var command = ValidCommand() with { SubHabits = new[] { new string('x', 201) } };

        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Sub-habit title must not exceed 200 characters"));
    }

    [Fact]
    public void Validate_ValidScheduledReminders_NoError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(
                ScheduledReminders: new List<ScheduledReminderTime>
                {
                    new(DayBefore, new TimeOnly(20, 0)),
                    new(SameDay, new TimeOnly(9, 0))
                })
        };

        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.ScheduledReminders : null);
    }

    [Fact]
    public void Validate_ScheduledReminders_Over5_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(
                ScheduledReminders: Enumerable.Range(0, 6)
                    .Select(i => new ScheduledReminderTime(SameDay, new TimeOnly(8 + i, 0)))
                    .ToList())
        };

        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.ScheduledReminders : null);
    }

    [Fact]
    public void Validate_ScheduledReminders_InvalidWhen_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(
                ScheduledReminders: new List<ScheduledReminderTime>
                {
                    new((ScheduledReminderWhen)999, new TimeOnly(9, 0))
                })
        };

        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.ScheduledReminders : null);
    }

    [Fact]
    public void Validate_ScheduledReminders_Duplicates_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(
                ScheduledReminders: new List<ScheduledReminderTime>
                {
                    new(SameDay, new TimeOnly(9, 0)),
                    new(SameDay, new TimeOnly(9, 0))
                })
        };

        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.ScheduledReminders : null);
    }

    [Fact]
    public void Validate_NullScheduledReminders_NoError()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.ScheduledReminders : null);
    }

    [Fact]
    public void Validate_ValidReminderTimes_NoError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(ReminderTimes: ValidReminderTimes)
        };

        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.ReminderTimes : null);
    }

    [Fact]
    public void Validate_ReminderTimes_Duplicates_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(ReminderTimes: DuplicateReminderTimes)
        };

        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.ReminderTimes : null);
    }

    [Fact]
    public void Validate_ReminderTimes_Negative_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(ReminderTimes: NegativeReminderTimes)
        };

        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.ReminderTimes : null);
    }

    [Fact]
    public void Validate_ReminderTimes_OverMax_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(ReminderTimes: OverMaxReminderTimes)
        };

        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.ReminderTimes : null);
    }

    [Fact]
    public void Validate_ReminderTimes_OverCountCap_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new HabitCommandOptions(
                ReminderTimes: Enumerable.Range(0, AppConstants.MaxReminderTimes + 1).ToList())
        };

        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.ReminderTimes : null);
    }
}
