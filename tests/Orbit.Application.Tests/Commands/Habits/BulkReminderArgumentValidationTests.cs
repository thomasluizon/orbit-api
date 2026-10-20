using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Habits;

public class BulkReminderArgumentValidationTests
{
    private static IAppConfigService Config()
    {
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(1));
        return config;
    }

    public static TheoryData<int[], string> InvalidReminderTimes => new()
    {
        { [-1], "VALIDATION_REMINDER_TIME_RANGE" },
        { [10081], "VALIDATION_REMINDER_TIME_RANGE" },
        { [15, 15], "VALIDATION_REMINDER_TIMES_UNIQUE" }
    };

    [Theory]
    [MemberData(nameof(InvalidReminderTimes))]
    public async Task CheckArguments_InvalidLaterChildReminderTimes_ReturnsDomainError(int[] offsets, string code)
    {
        var userId = Guid.NewGuid();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var command = new BulkCreateHabitsCommand(userId,
            [new("Root", null, null, null, ReminderTimes: [0, 10080], SubHabits:
            [
                new("Valid first child", null, null, null, ReminderTimes: [15]),
                new("Invalid later child", null, null, null, ReminderTimes: offsets)
            ])]);

        var result = await BulkCreateHabitsCommandHandler.CheckArgumentsAsync(command, dates, Config(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(code);
    }

    [Fact]
    public async Task CheckArguments_ValidChildOffsetsAndIndependentSettings_Pass()
    {
        var userId = Guid.NewGuid();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var command = new BulkCreateHabitsCommand(userId,
            [new("Root", null, null, null, ReminderTimes: [30], SubHabits:
            [
                new("Default", null, null, null),
                new("Empty", null, null, null, ReminderTimes: []),
                new("Boundaries", null, null, null, ReminderTimes: [10080, 0, 15],
                    ScheduledReminders: [new(ScheduledReminderWhen.DayBefore, new TimeOnly(8, 0))]),
                new("Relative", null, null, null, DueTime: new TimeOnly(9, 0), ReminderTimes: [0],
                    RelativeReminders: [new(-1439), new(-1), new(10080)])
            ])]);

        var result = await BulkCreateHabitsCommandHandler.CheckArgumentsAsync(command, dates, Config(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(99, false, false)]
    [InlineData(-1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(99, true, false)]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    public async Task CheckArguments_DirectChildReminderWhen_UsesDomainInvariant(int value, bool relative, bool valid)
    {
        var userId = Guid.NewGuid();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var child = new BulkHabitItem("Later child", null, null, null,
            DueTime: relative ? new TimeOnly(9, 0) : null,
            ScheduledReminders: relative ? null : [new ScheduledReminderTime((ScheduledReminderWhen)value, new TimeOnly(8, 0))],
            RelativeReminders: relative ? [new RelativeReminderTime(When: (ScheduledReminderWhen)value, Time: new TimeOnly(8, 0))] : null);
        var command = new BulkCreateHabitsCommand(userId,
            [new("Root", null, null, null, SubHabits: [new("First child", null, null, null), child])]);

        var result = await BulkCreateHabitsCommandHandler.CheckArgumentsAsync(command, dates, Config(), CancellationToken.None);

        result.IsSuccess.Should().Be(valid);
        if (!valid)
            result.ErrorCode.Should().Be(relative ? "INVALID_RELATIVE_REMINDERS" : "VALIDATION_SCHEDULED_REMINDER_WHEN");
    }
}
