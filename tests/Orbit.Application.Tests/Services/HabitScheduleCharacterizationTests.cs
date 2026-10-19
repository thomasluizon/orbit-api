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
        var habit = Habit.Create(new HabitCreateParams(
            Guid.NewGuid(), "Habit", type is 1 or 2 ? FrequencyUnit.Day : null,
            type is 1 or 2 ? 1 : null, Today,
            IsFlexible: type == 2, IsGeneral: type == 3,
            CreatedAtUtc: Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;

        HabitScheduleService.IsHabitDueOnDate(habit, Today.AddDays(-1)).Should().BeFalse();
        HabitScheduleService.IsHabitDueOnDate(habit, Today).Should().BeTrue();
        HabitScheduleService.IsHabitDueOnDate(habit, Today.AddDays(1)).Should().Be(dueTomorrow);
        habit.Log(Today, advanceDueDate: false).IsSuccess.Should().BeTrue();

        HabitMetricsCalculator.Calculate(habit, Today, 1, TimeZoneInfo.Utc)
            .Should().Be(new HabitMetrics(1, 1, 100m, 100m, 1, Today));
    }
}
