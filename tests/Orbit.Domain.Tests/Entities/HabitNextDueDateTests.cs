using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitNextDueDateTests
{
    [Fact]
    public void LogTenTimes_AdvancesNextDueDateWithoutMovingSelectedStart()
    {
        var start = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", FrequencyUnit.Day, 1, start)).Value;

        for (var offset = 0; offset < 10; offset++)
            habit.Log(start.AddDays(offset)).IsSuccess.Should().BeTrue();

        habit.NextDueDate.Should().Be(start.AddDays(10));
        habit.StartDate.Should().Be(start);
    }
}
