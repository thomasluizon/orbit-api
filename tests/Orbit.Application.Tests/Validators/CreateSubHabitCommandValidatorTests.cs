using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

using static Orbit.Domain.Enums.ScheduledReminderWhen;

namespace Orbit.Application.Tests.Validators;

public class CreateSubHabitCommandValidatorTests
{
    private readonly CreateSubHabitCommandValidator _validator = new();

    private static CreateSubHabitCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        ParentHabitId: Guid.NewGuid(),
        Title: "Sub Habit",
        Description: null);

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
    public void Validate_EmptyParentHabitId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { ParentHabitId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.ParentHabitId);
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
    public void Validate_ZeroFrequencyQuantity_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { FrequencyQuantity = 0 });
        result.ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Fact]
    public void Validate_NegativeFrequencyQuantity_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { FrequencyQuantity = -1 });
        result.ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Fact]
    public void Validate_NullFrequencyQuantity_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { FrequencyQuantity = null });
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
            Options = new HabitCommandOptions(Days: new[] { DayOfWeek.Monday })
        };

        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public void Validate_TooManyTags_HasError()
    {
        var tags = Enumerable.Range(0, AppConstants.MaxTagsPerHabit + 1)
            .Select(_ => Guid.NewGuid()).ToList();

        var result = _validator.TestValidate(ValidCommand() with { TagIds = tags });
        result.ShouldHaveValidationErrorFor(x => x.TagIds);
    }

    [Fact]
    public void Validate_ExactlyMaxTags_NoError()
    {
        var tags = Enumerable.Range(0, AppConstants.MaxTagsPerHabit)
            .Select(_ => Guid.NewGuid()).ToList();

        var result = _validator.TestValidate(ValidCommand() with { TagIds = tags });
        result.ShouldNotHaveValidationErrorFor(x => x.TagIds);
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
    public void Validate_DescriptionOver2000Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Description = new string('a', 2001) });
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void Validate_DescriptionExactly2000Chars_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Description = new string('a', 2000) });
        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }
}
