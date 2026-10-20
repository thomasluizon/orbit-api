using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitParentGuardsTests
{
    private static Habit Create() => Habit.Create(new HabitCreateParams(
        Guid.NewGuid(), "Habit", FrequencyUnit.Day, 1, new DateOnly(2026, 3, 20))).Value;

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
