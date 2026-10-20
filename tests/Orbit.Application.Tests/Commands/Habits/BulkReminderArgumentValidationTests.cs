using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Habits;

public class BulkReminderArgumentValidationTests
{
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

        var result = await BulkCreateHabitsCommandHandler.CheckArgumentsAsync(command, dates, CancellationToken.None);

        result.IsSuccess.Should().Be(valid);
        if (!valid)
            result.ErrorCode.Should().Be(relative ? "INVALID_RELATIVE_REMINDERS" : "VALIDATION_SCHEDULED_REMINDER_WHEN");
    }
}
