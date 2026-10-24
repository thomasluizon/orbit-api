using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Services;

public class HabitScheduleCharacterizationTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void ScheduleAndMetrics_KeepFourTypeBehavior(int type, bool dueTomorrow)
    {
        var habit = Habit.Create(new HabitCreateParams((HabitSchedule)type,
            Guid.NewGuid(), "Habit", type is 1 or 2 ? FrequencyUnit.Day : null,
            type is 1 or 2 ? 1 : null, Today,
            CreatedAtUtc: Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;

        HabitScheduleService.IsHabitDueOnDate(habit, Today.AddDays(-1)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Today).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Today.AddDays(1)).Should().Be(dueTomorrow);
        habit.Log(Today, advanceDueDate: false).IsSuccess.Should().BeTrue();

        HabitMetricsCalculator.Calculate(habit, Today, 1, TimeZoneInfo.Utc)
            .Should().Be(new HabitMetrics(1, 1, 100m, 100m, 1, Today));
    }

    [Theory]
    [InlineData(0, 1, 1, 100, 1, 0)]
    [InlineData(1, 1, 2, 75, 3, 3)]
    [InlineData(2, 1, 1, 50, 3, 3)]
    [InlineData(3, 1, 1, 100, 3, 3)]
    public void Metrics_WithGapsKeepTypeSpecificResults(
        int type, int currentStreak, int longestStreak, int rate, int completions, int lastOffset)
    {
        var habit = Habit.Create(new HabitCreateParams((HabitSchedule)type,
            Guid.NewGuid(), "Habit", type == 2 ? FrequencyUnit.Week : type == 1 ? FrequencyUnit.Day : null,
            type == 2 ? 2 : type == 1 ? 1 : null, Today,
            CreatedAtUtc: Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        habit.Log(Today, advanceDueDate: false).IsSuccess.Should().BeTrue();
        if (type != 0)
        {
            habit.Log(Today.AddDays(1), advanceDueDate: false).IsSuccess.Should().BeTrue();
            habit.Log(Today.AddDays(3), advanceDueDate: false).IsSuccess.Should().BeTrue();
        }

        HabitMetricsCalculator.Calculate(habit, Today.AddDays(3), 1, TimeZoneInfo.Utc)
            .Should().Be(new HabitMetrics(currentStreak, longestStreak, rate, rate, completions, Today.AddDays(lastOffset)));
    }

    [Theory]
    [InlineData(0, 0, true, false)]
    [InlineData(1, 1, false, true)]
    [InlineData(2, 0, true, true)]
    [InlineData(3, 0, true, false)]
    public void ScheduleAfterLog_KeepsSelectedStartSeparateFromCursor(
        int type, int cursorOffset, bool dueToday, bool dueTomorrow)
    {
        var habit = Habit.Create(new HabitCreateParams((HabitSchedule)type,
            Guid.NewGuid(), "Habit", type is 1 or 2 ? FrequencyUnit.Day : null,
            type is 1 or 2 ? 1 : null, Today,
            CreatedAtUtc: Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;

        habit.Log(Today).IsSuccess.Should().BeTrue();

        habit.StartDate.Should().Be(Today);
        habit.NextDueDate.Should().Be(Today.AddDays(cursorOffset));
        HabitScheduleService.IsHabitDueOnDate(habit, Today).Should().Be(dueToday);
        HabitScheduleService.IsHabitDueOnDate(habit, Today.AddDays(1)).Should().Be(dueTomorrow);
        HabitScheduleService.GetHistoricalScheduledDates(habit, Today, Today, TimeZoneInfo.Utc)
            .Should().Equal(Today);
        HabitMetricsCalculator.Calculate(habit, Today, 1, TimeZoneInfo.Utc)
            .Should().Be(new HabitMetrics(1, 1, 100m, 100m, 1, Today));
    }
}
