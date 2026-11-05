using FluentAssertions;
using FluentValidation.TestHelper;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Validators;

public class MonthDaysValidatorTests
{
    private readonly CreateHabitCommandValidator _create = new();
    private readonly UpdateHabitCommandValidator _update = new(Substitute.For<IGenericRepository<Habit>>());

    private static CreateHabitCommand Create(IReadOnlyList<int>? monthDays, FrequencyUnit? unit = FrequencyUnit.Month,
        HabitCommandOptions? options = null) =>
        new(Guid.NewGuid(), "Pagar contas", null, unit, 1, Options: options, MonthDays: monthDays);

    private static UpdateHabitCommand Update(IReadOnlyList<int>? monthDays, FrequencyUnit? unit = FrequencyUnit.Month) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Pagar contas", null, unit, 1, MonthDays: monthDays);

    [Fact]
    public void Create_ValidMonthDays_NoError()
    {
        _create.TestValidate(Create([8, 15])).ShouldNotHaveValidationErrorFor(x => x.MonthDays);
    }

    [Fact]
    public void Create_OmittedMonthDays_NoError()
    {
        _create.TestValidate(Create(null)).ShouldNotHaveValidationErrorFor(x => x.MonthDays);
    }

    public static TheoryData<int[]> InvalidDays() => new()
    {
        new[] { 0 },
        new[] { 32 },
        new[] { 8, 8 },
        Array.Empty<int>()
    };

    [Theory]
    [MemberData(nameof(InvalidDays))]
    public void Create_InvalidMonthDays_HasError(int[] monthDays)
    {
        _create.TestValidate(Create(monthDays)).ShouldHaveValidationErrorFor(x => x.MonthDays)
            .WithErrorCode(DomainErrors.MonthDaysInvalid.Code);
    }

    [Theory]
    [InlineData(FrequencyUnit.Day)]
    [InlineData(FrequencyUnit.Week)]
    [InlineData(FrequencyUnit.Year)]
    [InlineData(null)]
    public void Create_MonthDaysOnAnotherUnit_HasError(FrequencyUnit? unit)
    {
        _create.TestValidate(Create([8], unit)).ShouldHaveValidationErrorFor(x => x.MonthDays)
            .WithErrorCode(DomainErrors.MonthDaysRequireMonth.Code);
    }

    [Fact]
    public void Create_MonthDaysOnAFlexibleHabit_HasError()
    {
        _create.TestValidate(Create([8], options: new HabitCommandOptions(IsFlexible: true)))
            .ShouldHaveValidationErrorFor(x => x.MonthDays)
            .WithErrorCode(DomainErrors.MonthDaysRequireMonth.Code);
    }

    [Fact]
    public void Create_MonthDaysWithWeekdays_HasError()
    {
        _create.TestValidate(Create([8], options: new HabitCommandOptions(Days: [DayOfWeek.Monday])))
            .ShouldHaveValidationErrorFor(x => x.MonthDays)
            .WithErrorCode(DomainErrors.MonthDaysRequireMonth.Code);
    }

    [Fact]
    public async Task Update_EmptyMonthDays_ClearsWithoutError()
    {
        var result = await _update.TestValidateAsync(Update([]));

        result.ShouldNotHaveValidationErrorFor(x => x.MonthDays);
    }

    [Fact]
    public async Task Update_DuplicateMonthDays_HasError()
    {
        var result = await _update.TestValidateAsync(Update([15, 15]));

        result.ShouldHaveValidationErrorFor(x => x.MonthDays).WithErrorCode(DomainErrors.MonthDaysInvalid.Code);
    }

    [Fact]
    public async Task Update_MonthDaysOnAWeeklyHabit_HasError()
    {
        var result = await _update.TestValidateAsync(Update([8], FrequencyUnit.Week));

        result.ShouldHaveValidationErrorFor(x => x.MonthDays).WithErrorCode(DomainErrors.MonthDaysRequireMonth.Code);
    }

    [Fact]
    public void SubHabit_OutOfRangeMonthDays_HasError()
    {
        var command = new CreateSubHabitCommand(Guid.NewGuid(), Guid.NewGuid(), "Sub", null, MonthDays: [40]);

        new CreateSubHabitCommandValidator().TestValidate(command).ShouldHaveValidationErrorFor(x => x.MonthDays);
    }

    [Fact]
    public void BulkCreate_OutOfRangeMonthDays_HasError()
    {
        var command = new BulkCreateHabitsCommand(Guid.NewGuid(),
            [new BulkHabitItem("Pagar contas", null, FrequencyUnit.Month, 1, MonthDays: [0])]);

        new BulkCreateHabitsCommandValidator().TestValidate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task BulkUpdate_MonthDaysAloneIsAChange()
    {
        var command = new BulkUpdateHabitsCommand(Guid.NewGuid(), new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasMonthDays: true, MonthDays: [8, 15]));

        (await new BulkUpdateHabitsCommandValidator().ValidateAsync(command)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task BulkUpdate_DuplicateMonthDays_IsInvalid()
    {
        var command = new BulkUpdateHabitsCommand(Guid.NewGuid(), new BulkHabitFilter(true, []),
            new BulkHabitChanges(HasMonthDays: true, MonthDays: [8, 8]));

        (await new BulkUpdateHabitsCommandValidator().ValidateAsync(command)).IsValid.Should().BeFalse();
    }
}
