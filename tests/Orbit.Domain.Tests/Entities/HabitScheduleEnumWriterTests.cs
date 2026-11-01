using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class HabitScheduleEnumWriterTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);

    [Theory]
    [InlineData(HabitSchedule.OneTime, FrequencyUnit.Day)]
    [InlineData(HabitSchedule.Recurring, null)]
    [InlineData((HabitSchedule)99, null)]
    public void InvalidSchedule_CreateAndUpdateRejectWithoutMutation(HabitSchedule schedule, FrequencyUnit? unit)
    {
        Habit.Create(new HabitCreateParams(schedule, Guid.NewGuid(), "Habit", unit,
            unit.HasValue ? 1 : null, Start)).IsFailure.Should().BeTrue();
        var habit = Create(HabitSchedule.Recurring);
        habit.Update(new HabitUpdateParams(schedule, "Changed", null, unit,
            unit.HasValue ? 1 : null, null, false, null)).IsFailure.Should().BeTrue();
        habit.Schedule.Should().Be(HabitSchedule.Recurring);
        habit.Title.Should().Be("Habit");
        habit.StartDate.Should().Be(Start);
        habit.NextDueDate.Should().Be(Start);
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime, 0, true, true)]
    [InlineData(HabitSchedule.Recurring, 1, false, false)]
    [InlineData(HabitSchedule.Flexible, 0, false, false)]
    [InlineData(HabitSchedule.General, 0, true, true)]
    public void LogAndUnlog_UseScheduleForEachSchedule(
        HabitSchedule schedule, int cursorOffset, bool completed, bool checklistChecked)
    {
        var habit = Create(schedule);
        habit.UpdateChecklist([new ChecklistItem("Read", true)]);

        habit.Log(Start).IsSuccess.Should().BeTrue();

        habit.IsCompleted.Should().Be(completed);
        habit.NextDueDate.Should().Be(Start.AddDays(cursorOffset));
        habit.ChecklistItems.Should().Equal(new ChecklistItem("Read", checklistChecked));
        habit.Unlog(Start).IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
        habit.NextDueDate.Should().Be(Start);
        habit.StartDate.Should().Be(Start);
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime)]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public void Update_PreservesExplicitSchedule(HabitSchedule schedule)
    {
        var habit = Create(schedule);

        habit.Update(new HabitUpdateParams(habit.Schedule, "Renamed", null, habit.FrequencyUnit,
            habit.FrequencyQuantity, null, false, null)).IsSuccess.Should().BeTrue();

        habit.Schedule.Should().Be(schedule);
        habit.StartDate.Should().Be(Start);
        habit.NextDueDate.Should().Be(Start);
    }

    [Fact]
    public void FlexibleCompletionLimitAndSkip_UseScheduleForFlexibleSchedule()
    {
        var habit = Create(HabitSchedule.Flexible);

        habit.Log(Start).IsSuccess.Should().BeTrue();
        habit.Log(Start).IsSuccess.Should().BeTrue();
        habit.Logs.Select(log => log.CompletionOrdinal).Should().Equal(0, 1);
        habit.CheckLog(Start, 1).IsFailure.Should().BeTrue();
        habit.Unlog(Start).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(Start).IsSuccess.Should().BeTrue();
        habit.Logs.Should().ContainSingle(log => log.Value == 0);
        habit.CheckLog(Start, 1).IsFailure.Should().BeTrue();
    }

    private static Habit Create(HabitSchedule schedule) => Habit.Create(new HabitCreateParams(schedule,
        Guid.NewGuid(), "Habit", schedule is HabitSchedule.Recurring or HabitSchedule.Flexible ? FrequencyUnit.Day : null,
        schedule == HabitSchedule.Flexible ? 2 : schedule == HabitSchedule.Recurring ? 1 : null,
        Start)).Value;

}
