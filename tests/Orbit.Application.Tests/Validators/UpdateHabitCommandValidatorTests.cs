using FluentValidation.TestHelper;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Validators;

public class UpdateHabitCommandValidatorTests
{
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly UpdateHabitCommandValidator _validator;

    public UpdateHabitCommandValidatorTests()
    {
        _validator = new UpdateHabitCommandValidator(_habitRepository);
    }

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
    public async Task Validate_ValidCommand_NoErrors()
    {
        var command = ValidCommand();

        var result = await _validator.TestValidateAsync(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public async Task Validate_EmptyHabitId_HasError()
    {
        var command = ValidCommand() with { HabitId = Guid.Empty };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.HabitId);
    }

    [Fact]
    public async Task Validate_EmptyTitle_HasError()
    {
        var command = ValidCommand() with { Title = "" };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public async Task Validate_TitleOver200Chars_HasError()
    {
        var command = ValidCommand() with { Title = new string('a', 201) };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Theory]
    [InlineData(false, "Sub-habit title must not be empty")]
    [InlineData(true, "Sub-habit title must not exceed 200 characters")]
    public async Task Validate_ChildTitle_UsesSubHabitMessage(bool tooLong, string expectedMessage)
    {
        var command = ValidCommand();
        var child = Habit.Create(new HabitCreateParams(
            command.UserId, "Original child", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 9, 26),
            ParentHabitId: Guid.NewGuid())).Value;
        command = command with
        {
            HabitId = child.Id,
            Title = tooLong ? new string('a', AppConstants.MaxHabitTitleLength + 1) : ""
        };
        _habitRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()(child)
                ? child
                : null);

        var result = await _validator.TestValidateAsync(command);

        result.Errors.Single(e => e.PropertyName == nameof(UpdateHabitCommand.Title))
            .ErrorMessage.Should().Be(expectedMessage);
    }

    [Theory]
    [InlineData(false, "'Title' must not be empty.")]
    [InlineData(true, "The length of 'Title' must be 200 characters or fewer. You entered 201 characters.")]
    public async Task Validate_TopLevelTitle_UsesHabitMessage(bool tooLong, string expectedMessage)
    {
        var command = ValidCommand();
        var habit = Habit.Create(new HabitCreateParams(
            command.UserId, "Original habit", FrequencyUnit.Day, 1,
            DueDate: new DateOnly(2026, 9, 26))).Value;
        command = command with
        {
            HabitId = habit.Id,
            Title = tooLong ? new string('a', AppConstants.MaxHabitTitleLength + 1) : ""
        };
        _habitRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Expression<Func<Habit, bool>>>(0).Compile()(habit)
                ? habit
                : null);

        var result = await _validator.TestValidateAsync(command);

        result.Errors.Single(e => e.PropertyName == nameof(UpdateHabitCommand.Title))
            .ErrorMessage.Should().Be(expectedMessage);
    }

    [Fact]
    public async Task Validate_ZeroFrequencyQty_HasError()
    {
        var command = ValidCommand() with { FrequencyQuantity = 0 };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AppConstants.MaxIntervalWeeks)]
    public async Task Validate_ValidIntervalWeeks_NoError(int? intervalWeeks)
    {
        var result = await _validator.TestValidateAsync(ValidCommand() with { IntervalWeeks = intervalWeeks });

        result.ShouldNotHaveValidationErrorFor(x => x.IntervalWeeks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AppConstants.MaxIntervalWeeks + 1)]
    public async Task Validate_InvalidIntervalWeeks_HasError(int intervalWeeks)
    {
        var result = await _validator.TestValidateAsync(ValidCommand() with { IntervalWeeks = intervalWeeks });

        result.ShouldHaveValidationErrorFor(x => x.IntervalWeeks);
    }

    [Fact]
    public async Task Validate_DaysWithQtyNot1_HasError()
    {
        var command = ValidCommand() with
        {
            FrequencyQuantity = 2,
            Options = new UpdateHabitCommandOptions(Days: MondayOnly)
        };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public async Task Validate_DaysWithNonDayUnit_HasError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = FrequencyUnit.Week,
            FrequencyQuantity = 1,
            Options = new UpdateHabitCommandOptions(Days: MondayOnly)
        };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public async Task Validate_DaysWithDayUnitQty1_NoError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = FrequencyUnit.Day,
            FrequencyQuantity = 1,
            Options = new UpdateHabitCommandOptions(Days: MondayOnly)
        };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.Days : null);
    }

    [Fact]
    public async Task Validate_OneTimeTaskWithEndDate_HasError()
    {
        var command = ValidCommand() with
        {
            FrequencyUnit = null,
            FrequencyQuantity = null,
            IsGeneral = false,
            Options = new UpdateHabitCommandOptions(EndDate: new DateOnly(2030, 1, 1))
        };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Options != null ? x.Options.EndDate : null);
    }

    [Fact]
    public async Task Validate_RecurringTaskWithEndDate_NoError()
    {
        var command = ValidCommand() with
        {
            IsGeneral = false,
            Options = new UpdateHabitCommandOptions(EndDate: new DateOnly(2030, 1, 1))
        };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Options != null ? x.Options.EndDate : null);
    }

    [Fact]
    public async Task Validate_ReminderTimes_Duplicates_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new UpdateHabitCommandOptions(ReminderTimes: DuplicateReminderTimes)
        };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Options!.ReminderTimes);
    }

    [Fact]
    public async Task Validate_ReminderTimes_OutOfRange_HasError()
    {
        var command = ValidCommand() with
        {
            Options = new UpdateHabitCommandOptions(ReminderTimes: OutOfRangeReminderTimes)
        };

        var result = await _validator.TestValidateAsync(command);

        result.ShouldHaveValidationErrorFor(x => x.Options!.ReminderTimes);
    }
}
