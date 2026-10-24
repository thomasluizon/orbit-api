using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitParentGuardsTests
{
    private static Habit Create() => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
        Guid.NewGuid(), "Habit", FrequencyUnit.Day, 1, new DateOnly(2026, 3, 20))).Value;

    [Fact]
    public void SetParentHabitId_PreservesCadenceAnchorLogsAndCheckedChecklist()
    {
        var start = new DateOnly(2026, 1, 31);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, Guid.NewGuid(), "Monthly", FrequencyUnit.Month, 1, start,
            DueTime: new TimeOnly(9, 0), ReminderEnabled: true, ReminderTimes: [15],
            ChecklistItems: [new("Same", true), new("Same", false)])).Value;
        habit.Log(start).IsSuccess.Should().BeTrue();
        var due = habit.NextDueDate;
        var logs = habit.Logs.ToArray();
        var checklist = habit.ChecklistItems.ToArray();
        var parent = Guid.NewGuid();
        habit.SetParentHabitId(parent);
        habit.SetParentHabitId(parent);
        habit.SetParentHabitId(null);
        habit.StartDate.Should().Be(start);
        habit.NextDueDate.Should().Be(due);
        habit.OriginalDayOfMonth.Should().Be(31);
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Month);
        habit.FrequencyQuantity.Should().Be(1);
        habit.Logs.Should().Equal(logs);
        habit.ChecklistItems.Should().Equal(checklist);
        habit.ReminderEnabled.Should().BeTrue();
        habit.ReminderTimes.Should().Equal(15);
    }

    [Fact]
    public void SetParentHabitId_SelfParent_ThrowsWithoutMutation()
    {
        var habit = Create();
        var act = () => habit.SetParentHabitId(habit.Id);

        act.Should().Throw<ArgumentException>();
        habit.ParentHabitId.Should().BeNull();
    }

    [Fact]
    public void SetParentHabitId_EmptyParent_ThrowsWithoutMutation()
    {
        var habit = Create();
        var act = () => habit.SetParentHabitId(Guid.Empty);

        act.Should().Throw<ArgumentException>();
        habit.ParentHabitId.Should().BeNull();
    }

    [Fact]
    public void SetParentHabitId_ValidParentAndNull_AreAccepted()
    {
        var habit = Create();
        var parent = Guid.NewGuid();

        habit.SetParentHabitId(parent);
        habit.ParentHabitId.Should().Be(parent);
        habit.SetParentHabitId(null);
        habit.ParentHabitId.Should().BeNull();
    }
}
