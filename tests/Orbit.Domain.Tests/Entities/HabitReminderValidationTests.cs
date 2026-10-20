using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class HabitReminderValidationTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(2, false)]
    [InlineData(99, false)]
    [InlineData(-1, true)]
    [InlineData(2, true)]
    [InlineData(99, true)]
    public void Create_UndefinedScheduledWhen_FailsBeforeClockReminderFolding(int value, bool timed)
    {
        var result = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Reminder", null, null, Today,
            DueTime: timed ? new TimeOnly(9, 0) : null,
            ScheduledReminders: [new((ScheduledReminderWhen)value, new TimeOnly(8, 0))]));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("VALIDATION_SCHEDULED_REMINDER_WHEN");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(2, false)]
    [InlineData(99, false)]
    [InlineData(-1, true)]
    [InlineData(2, true)]
    [InlineData(99, true)]
    public void Update_UndefinedScheduledWhen_LeavesHabitUnchanged(int value, bool timed)
    {
        var existing = new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(7, 0));
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Reminder", null, null, Today,
            ScheduledReminders: [existing])).Value;

        var result = habit.Update(new HabitUpdateParams("Changed", null, null, null, null, false, null,
            DueTime: timed ? new TimeOnly(9, 0) : null,
            ScheduledReminders: [new((ScheduledReminderWhen)value, new TimeOnly(8, 0))]));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("VALIDATION_SCHEDULED_REMINDER_WHEN");
        habit.Title.Should().Be("Reminder");
        habit.DueTime.Should().BeNull();
        habit.ScheduledReminders.Should().Equal(existing);
        habit.RelativeReminders.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void UndefinedRelativeWhen_FailsCreateAndUpdate(int value)
    {
        RelativeReminderTime[] reminders = [new(When: (ScheduledReminderWhen)value, Time: new TimeOnly(8, 0))];
        var parameters = new HabitCreateParams(Guid.NewGuid(), "Reminder", null, null, Today,
            DueTime: new TimeOnly(9, 0));

        var create = Habit.Create(parameters with { RelativeReminders = reminders });
        create.IsFailure.Should().BeTrue();
        create.ErrorCode.Should().Be("INVALID_RELATIVE_REMINDERS");

        var habit = Habit.Create(parameters).Value;
        var update = habit.Update(new HabitUpdateParams("Changed", null, null, null, null, false, null,
            RelativeReminders: reminders));
        update.IsFailure.Should().BeTrue();
        update.ErrorCode.Should().Be("INVALID_RELATIVE_REMINDERS");
        habit.Title.Should().Be("Reminder");
        habit.RelativeReminders.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void DefinedScheduledWhen_PreservesCreateAndUpdateMeaning(int value, bool timed)
    {
        var reminder = new ScheduledReminderTime((ScheduledReminderWhen)value, new TimeOnly(8, 0));
        var parameters = new HabitCreateParams(Guid.NewGuid(), "Reminder", null, null, Today,
            DueTime: timed ? new TimeOnly(9, 0) : null,
            ScheduledReminders: [reminder]);
        var created = Habit.Create(parameters);
        created.IsSuccess.Should().BeTrue();
        created.Value.GetScheduledRemindersForLegacyClients().Should().Equal(reminder);

        var habit = Habit.Create(parameters with { ScheduledReminders = null }).Value;
        habit.Update(new HabitUpdateParams("Reminder", null, null, null, null, false, null,
                ScheduledReminders: [reminder])).IsSuccess.Should().BeTrue();
        habit.GetScheduledRemindersForLegacyClients().Should().Equal(reminder);
        habit.Update(new HabitUpdateParams("Reminder", null, null, null, null, false, null))
            .IsSuccess.Should().BeTrue();
        habit.GetScheduledRemindersForLegacyClients().Should().Equal(reminder);
        habit.Update(new HabitUpdateParams("Reminder", null, null, null, null, false, null,
                ScheduledReminders: [], RelativeReminders: [])).IsSuccess.Should().BeTrue();
        habit.GetScheduledRemindersForLegacyClients().Should().BeEmpty();
    }
}
