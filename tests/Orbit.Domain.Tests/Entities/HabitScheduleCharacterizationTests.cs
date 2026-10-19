using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class HabitScheduleCharacterizationTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(2, 0, false)]
    [InlineData(3, 0, false)]
    public void LogAndUnlog_KeepFourTypeBehavior(int type, int dueOffset, bool completed)
    {
        var habit = Create(type);
        var log = habit.Log(Start);
        log.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(Start.AddDays(dueOffset));
        habit.IsCompleted.Should().Be(completed);
        log.Value.Date.Should().Be(Start);
        log.Value.Value.Should().Be(1);

        habit.Unlog(Start).IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeFalse();
        habit.DueDate.Should().Be(Start);
        habit.Logs.Should().ContainSingle().Which.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public void CatchUpSkipAndPostpone_KeepDatesAndCompletion()
    {
        var recurring = Create(1);
        recurring.CatchUpDueDate(Start.AddDays(3)).IsSuccess.Should().BeTrue();
        recurring.DueDate.Should().Be(Start.AddDays(3));
        recurring.AdvanceDueDate(Start.AddDays(3)).IsSuccess.Should().BeTrue();
        recurring.DueDate.Should().Be(Start.AddDays(4));
        recurring.IsCompleted.Should().BeFalse();
        recurring.Logs.Should().BeEmpty();

        var flexible = Create(2);
        flexible.SkipFlexible(Start).IsSuccess.Should().BeTrue();
        flexible.DueDate.Should().Be(Start);
        flexible.IsCompleted.Should().BeFalse();
        flexible.Logs.Should().ContainSingle().Which.Value.Should().Be(0);

        var oneTime = Create(0);
        oneTime.PostponeTo(Start.AddDays(4));
        oneTime.DueDate.Should().Be(Start.AddDays(4));
        oneTime.IsCompleted.Should().BeFalse();
    }

    private static Habit Create(int type) => Habit.Create(new HabitCreateParams(
        Guid.NewGuid(), "Habit", type is 1 or 2 ? FrequencyUnit.Day : null,
        type is 1 or 2 ? 1 : null, Start,
        IsFlexible: type == 2, IsGeneral: type == 3)).Value;
}
