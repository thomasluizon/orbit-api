using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Services;

public class HabitScheduleMonthDaysTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static Habit CreateHabit(IReadOnlyList<int> monthDays, DateOnly start, int quantity = 1, bool isBadHabit = false)
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId,
            isBadHabit ? "Fumar" : "Pagar contas", FrequencyUnit.Month, quantity, start,
            IsBadHabit: isBadHabit, MonthDays: monthDays)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(habit,
            start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        return habit;
    }

    [Fact]
    public void GetScheduledDates_ReturnsOnlyTheChosenDaysOfEveryMonth()
    {
        var habit = CreateHabit([8, 15], new DateOnly(2026, 11, 1));

        var dates = HabitScheduleService.GetScheduledDates(habit, new DateOnly(2026, 11, 1), new DateOnly(2027, 1, 31));

        dates.Should().Equal(
            new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 15),
            new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 15),
            new DateOnly(2027, 1, 8), new DateOnly(2027, 1, 15));
    }

    [Theory]
    [InlineData(2027, 2, 28)]
    [InlineData(2028, 2, 29)]
    [InlineData(2027, 4, 30)]
    [InlineData(2027, 5, 31)]
    public void IsHabitDueOnDate_DayThirtyOneFallsOnTheLastDayOfAShortMonth(int year, int month, int day)
    {
        var habit = CreateHabit([31], new DateOnly(2027, 1, 1));
        var target = new DateOnly(year, month, day);

        HabitScheduleService.IsHabitDueOnDate(habit, target).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, target.AddDays(-1)).Should().BeFalse();
    }

    [Fact]
    public void GetScheduledDates_DayThirtyOne_HasOneOccurrencePerMonth()
    {
        var habit = CreateHabit([30, 31], new DateOnly(2027, 1, 1));

        var dates = HabitScheduleService.GetScheduledDates(habit, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28));

        dates.Should().Equal(new DateOnly(2027, 2, 28));
    }

    [Fact]
    public void GetScheduledDates_WithAMonthQuantity_SkipsTheInactiveMonths()
    {
        var habit = CreateHabit([8], new DateOnly(2027, 1, 1), quantity: 2);

        var dates = HabitScheduleService.GetScheduledDates(habit, new DateOnly(2027, 1, 1), new DateOnly(2027, 5, 31));

        dates.Should().Equal(new DateOnly(2027, 1, 8), new DateOnly(2027, 3, 8), new DateOnly(2027, 5, 8));
    }

    [Fact]
    public void HistoricalScheduledDates_FeedTheStreakWithTheChosenDaysOnly()
    {
        var habit = CreateHabit([8, 15], new DateOnly(2026, 11, 1));
        habit.Log(new DateOnly(2026, 11, 8));
        habit.Log(new DateOnly(2026, 11, 15));
        habit.Log(new DateOnly(2026, 12, 8));

        var history = HabitScheduleService.GetHistoricalScheduledDates(
            habit, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 20), TimeZoneInfo.Utc);
        var union = HabitScheduleService.GetUnionScheduledDatesForStreak(
            [habit], new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 20), TimeZoneInfo.Utc);

        history.Should().Equal(
            new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 15),
            new DateOnly(2026, 12, 8), new DateOnly(2026, 12, 15));
        var expected = history.ToHashSet();
        var completions = new HashSet<DateOnly>
        {
            new(2026, 11, 8), new(2026, 11, 15), new(2026, 12, 8)
        };
        HabitScheduleService.ComputeStreakAsOf(expected, completions, [], new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 14))
            .Streak.Should().Be(3);
        HabitScheduleService.ComputeStreakAsOf(expected, completions, [], new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 16))
            .Streak.Should().Be(0);
        union.Should().Contain(new DateOnly(2026, 12, 15)).And.NotContain(new DateOnly(2026, 12, 14));
    }

    [Fact]
    public void IsOverdueOnDate_OnlyAfterAMissedChosenDay()
    {
        var habit = CreateHabit([8, 15], new DateOnly(2026, 11, 1));

        HabitScheduleService.IsOverdueOnDate(habit, new DateOnly(2026, 11, 8)).Should().BeFalse();
        HabitScheduleService.IsOverdueOnDate(habit, new DateOnly(2026, 11, 9)).Should().BeTrue();
    }

    [Fact]
    public void WasScheduledOnDate_BadHabitSlipRulesReadTheChosenDays()
    {
        var habit = CreateHabit([8, 15], new DateOnly(2026, 11, 1), isBadHabit: true);

        HabitScheduleService.WasScheduledOnDate(habit, new DateOnly(2026, 11, 15), 1).Should().BeTrue();
        HabitScheduleService.WasScheduledOnDate(habit, new DateOnly(2026, 11, 16), 1).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetScheduledDates_AfterAnAddedDay_ListsTheDayBeforeTheOldCursor(bool resendStartDate)
    {
        var habit = CreateHabit([10], new DateOnly(2027, 1, 10));
        habit.Log(new DateOnly(2027, 1, 10)).IsSuccess.Should().BeTrue();

        habit.Update(new HabitUpdateParams(habit.Schedule, habit.Title, null, FrequencyUnit.Month, 1, null, false, null,
            UserToday: new DateOnly(2027, 1, 11), StartDate: resendStartDate ? habit.StartDate : null,
            MonthDays: [10, 15])).IsSuccess.Should().BeTrue();

        HabitScheduleService.IsHabitDueOnDate(habit, new DateOnly(2027, 1, 15)).Should().BeTrue();
        HabitScheduleService.GetScheduledDates(habit, new DateOnly(2027, 1, 11), new DateOnly(2027, 2, 28))
            .Should().Equal(new DateOnly(2027, 1, 15), new DateOnly(2027, 2, 10), new DateOnly(2027, 2, 15));
    }
}
