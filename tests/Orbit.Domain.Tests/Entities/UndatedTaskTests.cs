using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class UndatedTaskTests
{
    private static readonly DateOnly LoggedDate = new(2026, 4, 2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Log_CompletesPermanently(bool repeat)
    {
        var task = Create(repeat);

        task.Log(LoggedDate).IsSuccess.Should().BeTrue();

        task.IsCompleted.Should().BeTrue();
        task.NextDueDate.Should().Be(LoggedDate);
        task.RepeatIntervalMinutes.Should().Be(repeat ? 480 : null);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void SecondLog_ReturnsCompletedTaskError(bool repeat, int dayOffset)
    {
        var task = Create(repeat);
        task.Log(LoggedDate).IsSuccess.Should().BeTrue();

        var result = task.Log(LoggedDate.AddDays(dayOffset));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.CannotLogCompletedHabit.Code);
        task.Logs.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unlog_ReopensAndAllowsCompletionAgain(bool repeat)
    {
        var task = Create(repeat);
        task.Log(LoggedDate).IsSuccess.Should().BeTrue();
        task.IsCompleted.Should().BeTrue();

        task.Unlog(LoggedDate).IsSuccess.Should().BeTrue();

        task.IsCompleted.Should().BeFalse();
        task.Log(LoggedDate.AddDays(1)).IsSuccess.Should().BeTrue();
        task.IsCompleted.Should().BeTrue();
        task.Logs.Should().ContainSingle(log => !log.IsDeleted);
    }

    private static Habit Create(bool repeat) => Habit.Create(new HabitCreateParams(
        HabitSchedule.General, Guid.NewGuid(), "Undated task", null, null, DueDate: LoggedDate,
        RepeatReminder: repeat ? new RepeatReminderOptions(480) : null)).Value;
}
