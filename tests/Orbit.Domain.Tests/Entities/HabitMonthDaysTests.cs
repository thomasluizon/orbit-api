using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitMonthDaysTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static Result<Habit> CreateMonthly(
        IReadOnlyList<int>? monthDays,
        DateOnly dueDate,
        int quantity = 1,
        FrequencyUnit unit = FrequencyUnit.Month,
        HabitSchedule schedule = HabitSchedule.Recurring,
        IReadOnlyList<DayOfWeek>? days = null,
        int? intervalWeeks = null,
        DateOnly? startDate = null) =>
        Habit.Create(new HabitCreateParams(schedule, UserId, "Pagar contas", unit, quantity, dueDate,
            Days: days, IntervalWeeks: intervalWeeks, StartDate: startDate, MonthDays: monthDays));

    private static HabitUpdateParams UpdateFor(Habit habit, IReadOnlyList<int>? monthDays,
        FrequencyUnit? unit = FrequencyUnit.Month, int? quantity = 1) =>
        new(habit.Schedule, habit.Title, habit.Description, unit, quantity, null, habit.IsBadHabit, null,
            MonthDays: monthDays);

    [Fact]
    public void Create_AlignsTheFirstOccurrenceToTheNextChosenDay()
    {
        var habit = CreateMonthly([15, 8], new DateOnly(2026, 11, 5)).Value;

        habit.MonthDays.Should().Equal(8, 15);
        habit.NextDueDate.Should().Be(new DateOnly(2026, 11, 8));
        habit.ScheduledStartDate.Should().Be(new DateOnly(2026, 11, 8));
    }

    [Fact]
    public void Create_WithStartDateAfterTheLastChosenDay_MovesToTheNextMonth()
    {
        var habit = CreateMonthly([8, 15], new DateOnly(2026, 11, 20), startDate: new DateOnly(2026, 11, 20)).Value;

        habit.NextDueDate.Should().Be(new DateOnly(2026, 12, 8));
    }

    [Fact]
    public void Log_AdvancesThroughTheChosenDaysOfEveryMonth()
    {
        var habit = CreateMonthly([8, 15], new DateOnly(2026, 11, 1)).Value;
        var seen = new List<DateOnly>();

        for (var i = 0; i < 5; i++)
        {
            seen.Add(habit.NextDueDate);
            habit.Log(habit.NextDueDate).IsSuccess.Should().BeTrue();
        }

        seen.Should().Equal(
            new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 15),
            new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 15),
            new DateOnly(2027, 1, 8));
    }

    [Fact]
    public void Log_PutsAMissingDayOnTheLastDayOfAShortMonth()
    {
        var habit = CreateMonthly([31], new DateOnly(2027, 1, 1)).Value;
        var seen = new List<DateOnly>();

        for (var i = 0; i < 5; i++)
        {
            seen.Add(habit.NextDueDate);
            habit.Log(habit.NextDueDate).IsSuccess.Should().BeTrue();
        }

        seen.Should().Equal(
            new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31),
            new DateOnly(2027, 4, 30), new DateOnly(2027, 5, 31));
    }

    [Fact]
    public void Log_FoldsTwoMissingDaysIntoOneOccurrence()
    {
        var habit = CreateMonthly([30, 31], new DateOnly(2028, 2, 1)).Value;

        habit.NextDueDate.Should().Be(new DateOnly(2028, 2, 29));
        habit.Log(habit.NextDueDate);
        habit.NextDueDate.Should().Be(new DateOnly(2028, 3, 30));
    }

    [Fact]
    public void Log_WithAMonthQuantity_SkipsTheInactiveMonths()
    {
        var habit = CreateMonthly([8, 15], new DateOnly(2027, 1, 1), quantity: 2).Value;

        habit.Log(habit.NextDueDate);
        habit.Log(habit.NextDueDate);

        habit.NextDueDate.Should().Be(new DateOnly(2027, 3, 8));
    }

    [Fact]
    public void CatchUpDueDate_LandsOnTheNextChosenDay()
    {
        var habit = CreateMonthly([8, 15], new DateOnly(2026, 11, 1)).Value;

        habit.CatchUpDueDate(new DateOnly(2027, 1, 10));

        habit.NextDueDate.Should().Be(new DateOnly(2027, 1, 15));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(-1)]
    public void Create_RejectsADayOutOfRange(int day)
    {
        var result = CreateMonthly([8, day], new DateOnly(2026, 11, 1));

        result.ErrorCode.Should().Be(DomainErrors.MonthDaysInvalid.Code);
    }

    [Fact]
    public void Create_RejectsDuplicateDays()
    {
        var result = CreateMonthly([8, 8], new DateOnly(2026, 11, 1));

        result.ErrorCode.Should().Be(DomainErrors.MonthDaysInvalid.Code);
    }

    [Theory]
    [InlineData(FrequencyUnit.Day)]
    [InlineData(FrequencyUnit.Week)]
    [InlineData(FrequencyUnit.Year)]
    public void Create_RejectsMonthDaysOnAnotherUnit(FrequencyUnit unit)
    {
        var result = CreateMonthly([8], new DateOnly(2026, 11, 1), unit: unit);

        result.ErrorCode.Should().Be(DomainErrors.MonthDaysRequireMonth.Code);
    }

    [Fact]
    public void Create_RejectsMonthDaysOnAFlexibleHabit()
    {
        var result = CreateMonthly([8], new DateOnly(2026, 11, 1), schedule: HabitSchedule.Flexible);

        result.ErrorCode.Should().Be(DomainErrors.MonthDaysRequireMonth.Code);
    }

    [Fact]
    public void Create_RejectsMonthDaysWithAWeekInterval()
    {
        var result = CreateMonthly([8], new DateOnly(2026, 11, 1), intervalWeeks: 2);

        result.ErrorCode.Should().Be(DomainErrors.MonthDaysRequireMonth.Code);
    }

    [Fact]
    public void Create_WithoutMonthDays_KeepsTheAnchorDaySchedule()
    {
        var habit = CreateMonthly(null, new DateOnly(2026, 11, 5)).Value;

        habit.MonthDays.Should().BeEmpty();
        habit.NextDueDate.Should().Be(new DateOnly(2026, 11, 5));
        habit.Log(habit.NextDueDate);
        habit.NextDueDate.Should().Be(new DateOnly(2026, 12, 5));
    }

    [Fact]
    public void Update_OmittedMonthDays_KeepsTheStoredDays()
    {
        var habit = CreateMonthly([8, 15], new DateOnly(2026, 11, 1)).Value;

        habit.Update(UpdateFor(habit, monthDays: null)).IsSuccess.Should().BeTrue();

        habit.MonthDays.Should().Equal(8, 15);
        habit.NextDueDate.Should().Be(new DateOnly(2026, 11, 8));
    }

    [Fact]
    public void Update_EmptyMonthDays_ClearsTheDays()
    {
        var habit = CreateMonthly([8, 15], new DateOnly(2026, 11, 1)).Value;

        habit.Update(UpdateFor(habit, monthDays: [])).IsSuccess.Should().BeTrue();

        habit.MonthDays.Should().BeEmpty();
    }

    [Fact]
    public void Update_ToAnotherUnit_DropsTheOmittedMonthDays()
    {
        var habit = CreateMonthly([8, 15], new DateOnly(2026, 11, 1)).Value;

        habit.Update(UpdateFor(habit, monthDays: null, unit: FrequencyUnit.Week)).IsSuccess.Should().BeTrue();

        habit.MonthDays.Should().BeEmpty();
    }

    [Fact]
    public void Update_NewMonthDays_RealignsTheDueDate()
    {
        var habit = CreateMonthly(null, new DateOnly(2026, 11, 5)).Value;

        habit.Update(UpdateFor(habit, monthDays: [20, 10])).IsSuccess.Should().BeTrue();

        habit.MonthDays.Should().Equal(10, 20);
        habit.NextDueDate.Should().Be(new DateOnly(2026, 11, 10));
    }

    [Fact]
    public void Update_RejectsMonthDaysOnAnotherUnit()
    {
        var habit = CreateMonthly(null, new DateOnly(2026, 11, 5)).Value;

        var result = habit.Update(UpdateFor(habit, monthDays: [8], unit: FrequencyUnit.Week));

        result.ErrorCode.Should().Be(DomainErrors.MonthDaysRequireMonth.Code);
    }
}
