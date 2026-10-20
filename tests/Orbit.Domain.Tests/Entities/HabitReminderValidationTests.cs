using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class HabitReminderValidationTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);

    public static TheoryData<int[], string, string> InvalidReminderTimes => new()
    {
        { [-1], "VALIDATION_REMINDER_TIME_RANGE", "Set reminders from 0 to 10080 minutes before the due time." },
        { [10081], "VALIDATION_REMINDER_TIME_RANGE", "Set reminders from 0 to 10080 minutes before the due time." },
        { [15, -1], "VALIDATION_REMINDER_TIME_RANGE", "Set reminders from 0 to 10080 minutes before the due time." },
        { [15, 10081], "VALIDATION_REMINDER_TIME_RANGE", "Set reminders from 0 to 10080 minutes before the due time." },
        { [15, 15], "VALIDATION_REMINDER_TIMES_UNIQUE", "Remove duplicate reminder times." }
    };

    [Theory]
    [MemberData(nameof(InvalidReminderTimes))]
    public void Create_InvalidReminderTimes_ReturnsPreciseDomainError(int[] offsets, string code, string message)
    {
        var result = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Reminder", null, null, Today,
            ReminderTimes: offsets));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(code);
        result.Error.Should().Be(message);
    }

    [Theory]
    [MemberData(nameof(InvalidReminderTimes))]
    public void Update_InvalidReminderTimes_LeavesHabitUnchanged(int[] offsets, string code, string message)
    {
        var clock = new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(7, 0));
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Reminder", null, null, Today,
            Description: "Original", ReminderEnabled: true, ReminderTimes: [30], ScheduledReminders: [clock])).Value;

        var result = habit.Update(new HabitUpdateParams("Changed", "Changed", null, null, null, false, Today.AddDays(1),
            DueTime: new TimeOnly(9, 0), ReminderEnabled: false, ReminderTimes: offsets,
            ScheduledReminders: [], RelativeReminders: [new(-1)]));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(code);
        result.Error.Should().Be(message);
        habit.Title.Should().Be("Reminder");
        habit.Description.Should().Be("Original");
        habit.DueDate.Should().Be(Today);
        habit.DueTime.Should().BeNull();
        habit.ReminderEnabled.Should().BeTrue();
        habit.ReminderTimes.Should().Equal(30);
        habit.ScheduledReminders.Should().Equal(clock);
        habit.RelativeReminders.Should().BeEmpty();
    }

    public static TheoryData<int[]?> ValidReminderTimes => new()
    {
        (int[]?)null,
        Array.Empty<int>(),
        new[] { 0 },
        new[] { 10080 },
        new[] { 10080, 0, 15 }
    };

    [Theory]
    [MemberData(nameof(ValidReminderTimes))]
    public void ReminderTimes_ValidValues_PreserveCreateAndUpdateSemantics(int[]? offsets)
    {
        var parameters = new HabitCreateParams(Guid.NewGuid(), "Reminder", null, null, Today, ReminderTimes: offsets);
        var created = Habit.Create(parameters);
        created.IsSuccess.Should().BeTrue();
        created.Value.ReminderTimes.Should().Equal(offsets ?? [15]);

        var habit = Habit.Create(parameters with { ReminderTimes = [30] }).Value;
        habit.Update(new HabitUpdateParams("Reminder", null, null, null, null, false, null,
            ReminderTimes: offsets)).IsSuccess.Should().BeTrue();
        habit.ReminderTimes.Should().Equal(offsets ?? [30]);
    }

    [Theory]
    [InlineData(-1439)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10080)]
    public void RelativeMinutesBefore_ValidRange_RemainsIndependentOfReminderTimes(int offset)
    {
        RelativeReminderTime[] relative = [new(offset)];
        var parameters = new HabitCreateParams(Guid.NewGuid(), "Reminder", null, null, Today,
            DueTime: new TimeOnly(9, 0), ReminderTimes: [0, 10080], RelativeReminders: relative);
        var created = Habit.Create(parameters);
        created.IsSuccess.Should().BeTrue();
        created.Value.ReminderTimes.Should().Equal(0, 10080);
        created.Value.RelativeReminders.Should().Equal(relative);

        var habit = Habit.Create(parameters with { RelativeReminders = [] }).Value;
        habit.Update(new HabitUpdateParams("Reminder", null, null, null, null, false, null,
            DueTime: new TimeOnly(9, 0), RelativeReminders: relative)).IsSuccess.Should().BeTrue();
        habit.ReminderTimes.Should().Equal(0, 10080);
        habit.RelativeReminders.Should().Equal(relative);
    }

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
