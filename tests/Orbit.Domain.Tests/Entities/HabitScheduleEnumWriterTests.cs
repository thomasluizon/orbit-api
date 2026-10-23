using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class HabitScheduleEnumWriterTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);

    [Theory]
    [InlineData(HabitSchedule.OneTime, 0, true, true)]
    [InlineData(HabitSchedule.Recurring, 1, false, false)]
    [InlineData(HabitSchedule.Flexible, 0, false, false)]
    [InlineData(HabitSchedule.General, 0, false, true)]
    public void LogAndUnlog_UseScheduleDespiteDivergentLegacyFlags(
        HabitSchedule schedule, int cursorOffset, bool completed, bool checklistChecked)
    {
        var habit = Create(schedule);
        DivergeLegacyFlags(habit);
        habit.UpdateChecklist([new ChecklistItem("Read", true)]);

        habit.Log(Start).IsSuccess.Should().BeTrue();

        habit.IsCompleted.Should().Be(completed);
        habit.DueDate.Should().Be(Start.AddDays(cursorOffset));
        habit.ChecklistItems.Should().Equal(new ChecklistItem("Read", checklistChecked));
        habit.Unlog(Start).IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
        habit.DueDate.Should().Be(Start);
        habit.StartDate.Should().Be(Start);
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime)]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public void Update_OmittedFlagsUseScheduleAndResynchronizeLegacyState(HabitSchedule schedule)
    {
        var habit = Create(schedule);
        DivergeLegacyFlags(habit);

        habit.Update(new HabitUpdateParams("Renamed", null, habit.FrequencyUnit,
            habit.FrequencyQuantity, null, false, null)).IsSuccess.Should().BeTrue();

        habit.Schedule.Should().Be(schedule);
        habit.IsGeneral.Should().Be(schedule == HabitSchedule.General);
        habit.IsFlexible.Should().Be(schedule == HabitSchedule.Flexible);
        habit.StartDate.Should().Be(Start);
        habit.DueDate.Should().Be(Start);
    }

    [Fact]
    public void FlexibleCompletionLimitAndSkip_UseScheduleDespiteDivergentFlags()
    {
        var habit = Create(HabitSchedule.Flexible);
        DivergeLegacyFlags(habit);

        habit.Log(Start).IsSuccess.Should().BeTrue();
        habit.Log(Start).IsSuccess.Should().BeTrue();
        habit.Logs.Select(log => log.CompletionOrdinal).Should().Equal(0, 1);
        habit.CheckLog(Start, 1).IsFailure.Should().BeTrue();
        habit.Unlog(Start).IsSuccess.Should().BeTrue();
        habit.SkipFlexible(Start).IsSuccess.Should().BeTrue();
        habit.Logs.Should().ContainSingle(log => log.Value == 0);
        habit.CheckLog(Start, 1).IsFailure.Should().BeTrue();
    }

    private static Habit Create(HabitSchedule schedule) => Habit.Create(new HabitCreateParams(
        Guid.NewGuid(), "Habit", schedule is HabitSchedule.Recurring or HabitSchedule.Flexible ? FrequencyUnit.Day : null,
        schedule == HabitSchedule.Flexible ? 2 : schedule == HabitSchedule.Recurring ? 1 : null,
        Start, IsGeneral: schedule == HabitSchedule.General, IsFlexible: schedule == HabitSchedule.Flexible)).Value;

    private static void DivergeLegacyFlags(Habit habit)
    {
        typeof(Habit).GetProperty(nameof(Habit.IsGeneral))!.SetValue(habit, !habit.IsGeneral);
        typeof(Habit).GetProperty(nameof(Habit.IsFlexible))!.SetValue(habit, !habit.IsFlexible);
    }
}
