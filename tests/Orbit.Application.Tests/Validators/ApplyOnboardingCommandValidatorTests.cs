using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public class ApplyOnboardingCommandValidatorTests
{
    private readonly ApplyOnboardingCommandValidator _validator = new();

    private static ApplyHabitInput ValidHabit() =>
        new("Drink water", null, null, FrequencyUnit.Day, 1);

    private static ApplyGoalInput ValidGoal() =>
        new("Run 100km", null, 100, "km");

    private static ApplyOnboardingCommand Valid() => new(
        UserId: Guid.NewGuid(),
        Habits: new[] { ValidHabit() },
        FirstLog: null,
        Goal: ValidGoal(),
        WeekStartDay: 1,
        ColorScheme: "purple");

    [Fact]
    public void Valid_NoErrors() =>
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void NullHabits_HasError()
    {
        var command = Valid() with { Habits = null! };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Habits);
    }

    [Fact]
    public void TooManyHabits_HasError()
    {
        var habits = Enumerable.Range(0, AppConstants.MaxBulkOperationSize + 1).Select(_ => ValidHabit()).ToArray();
        var command = Valid() with { Habits = habits };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor("Habits.Count");
    }

    [Fact]
    public void HabitWithEmptyTitle_HasError()
    {
        var command = Valid() with { Habits = new[] { ValidHabit() with { Title = "" } } };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor("Habits[0].Title");
    }

    [Fact]
    public void InvalidGoal_HasError()
    {
        var command = Valid() with { Goal = ValidGoal() with { Title = "" } };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor("Goal.Title");
    }

    [Fact]
    public void NullGoal_NoGoalError()
    {
        var command = Valid() with { Goal = null };

        _validator.TestValidate(command).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SupportedWeekStartDay_NoError(int day)
    {
        var command = Valid() with { WeekStartDay = day };

        _validator.TestValidate(command).ShouldNotHaveValidationErrorFor(x => x.WeekStartDay!.Value);
    }

    [Fact]
    public void UnsupportedWeekStartDay_HasError()
    {
        var command = Valid() with { WeekStartDay = 3 };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.WeekStartDay!.Value);
    }
}

public class ApplyHabitInputValidatorTests
{
    private readonly ApplyHabitInputValidator _validator = new();

    [Fact]
    public void Valid_NoErrors() =>
        _validator.TestValidate(new ApplyHabitInput("Read", null, null, FrequencyUnit.Day, 1))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void EmptyTitle_HasError() =>
        _validator.TestValidate(new ApplyHabitInput("", null, null, FrequencyUnit.Day, 1))
            .ShouldHaveValidationErrorFor(x => x.Title);

    [Fact]
    public void ZeroFrequencyQuantity_HasError() =>
        _validator.TestValidate(new ApplyHabitInput("Read", null, null, FrequencyUnit.Day, 0))
            .ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);

    [Fact]
    public void FrequencyUnitWithoutQuantity_HasError() =>
        _validator.TestValidate(new ApplyHabitInput("Read", null, null, FrequencyUnit.Day, null))
            .ShouldHaveValidationErrorFor(x => x.FrequencyQuantity);
}

public class ApplyGoalInputValidatorTests
{
    private readonly ApplyGoalInputValidator _validator = new();

    private static ApplyGoalInput Valid() => new("Run 100km", null, 100, "km");

    [Fact]
    public void Valid_NoErrors() =>
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void EmptyTitle_HasError() =>
        _validator.TestValidate(Valid() with { Title = "" }).ShouldHaveValidationErrorFor(x => x.Title);

    [Fact]
    public void DescriptionTooLong_HasError() =>
        _validator.TestValidate(Valid() with { Description = new string('d', AppConstants.MaxGoalDescriptionLength + 1) })
            .ShouldHaveValidationErrorFor(x => x.Description);

    [Fact]
    public void NonPositiveTargetValue_HasError() =>
        _validator.TestValidate(Valid() with { TargetValue = 0 }).ShouldHaveValidationErrorFor(x => x.TargetValue);

    [Fact]
    public void EmptyUnit_HasError() =>
        _validator.TestValidate(Valid() with { Unit = "" }).ShouldHaveValidationErrorFor(x => x.Unit);

    [Fact]
    public void UnitTooLong_HasError() =>
        _validator.TestValidate(Valid() with { Unit = new string('u', 51) }).ShouldHaveValidationErrorFor(x => x.Unit);

    [Fact]
    public void InvalidType_HasError() =>
        _validator.TestValidate(Valid() with { Type = (GoalType)999 }).ShouldHaveValidationErrorFor(x => x.Type);
}
