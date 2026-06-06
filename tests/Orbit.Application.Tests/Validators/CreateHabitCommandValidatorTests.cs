using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

using static Orbit.Domain.Enums.ScheduledReminderWhen;

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
            Options = new HabitCommandOptions(Days: new[] { DayOfWeek.Monday })
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
            Options = new HabitCommandOptions(Days: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday })
        };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
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
}
