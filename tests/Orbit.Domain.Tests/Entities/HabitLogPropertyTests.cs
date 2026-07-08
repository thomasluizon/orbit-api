using FluentAssertions;
using FsCheck.Xunit;
using Orbit.Domain.Entities;
using Orbit.Tests.Generators;

namespace Orbit.Domain.Tests.Entities;

[Properties(Arbitrary = new[] { typeof(OrbitArbitraries) }, MaxTest = 100, Replay = "(10000019,10000079)")]
public class HabitLogPropertyTests
{
    [Property]
    public void SkipFlexible_AlwaysProducesZeroValueLog(FlexibleHabit flexible, DateOnly date)
    {
        var result = flexible.Habit.SkipFlexible(date);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(0m);
    }

    [Property]
    public void Log_OnFreshHabit_ProducesOneValueLog(Habit habit, DateOnly date)
    {
        var result = habit.Log(date);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(1m);
    }

    [Property]
    public void LogThenUnlog_OnDueDate_RestoresObservableState(Habit habit)
    {
        var dueDate = habit.DueDate;
        var wasCompleted = habit.IsCompleted;

        habit.Log(dueDate);
        var unlog = habit.Unlog(dueDate);

        unlog.IsSuccess.Should().BeTrue();
        habit.Logs.Count(l => l.Value > 0 && !l.IsDeleted).Should().Be(0);
        habit.IsCompleted.Should().Be(wasCompleted);
        habit.DueDate.Should().Be(dueDate);
    }
}
