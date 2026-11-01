using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class RepeatReminderTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(1380)]
    public void StartMustLeaveRoomForTheInterval(int minutes)
    {
        var lastStartAtUtc = DateTime.SpecifyKind(DateTime.MaxValue.AddMinutes(-minutes), DateTimeKind.Utc);
        var valid = new RepeatReminderOptions(minutes, StartAtUtc: lastStartAtUtc);
        valid.IsValid().Should().BeTrue();
        Habit.Create(Parameters(valid)).IsSuccess.Should().BeTrue();
        var invalid = valid with { StartAtUtc = lastStartAtUtc.AddTicks(1) };
        invalid.IsValid().Should().BeFalse();
        Habit.Create(Parameters(invalid)).IsFailure.Should().BeTrue();
        var habit = Habit.Create(Parameters(new(minutes))).Value;
        habit.Update(new HabitUpdateParams(habit.Schedule, habit.Title, null, FrequencyUnit.Day, 1,
            [], false, habit.NextDueDate, RepeatReminder: invalid)).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(480)]
    [InlineData(1380)]
    public void DailyHabit_AcceptsSupportedIntervals(int minutes)
    {
        var result = Habit.Create(Parameters(new(minutes)));
        result.IsSuccess.Should().BeTrue();
        result.Value.RepeatReminder!.IntervalMinutes.Should().Be(minutes);
        result.Value.RepeatReminder.StartAtUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(90)]
    [InlineData(1440)]
    public void DailyHabit_RejectsUnsupportedIntervals(int minutes) =>
        Habit.Create(Parameters(new(minutes))).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData(HabitSchedule.Recurring, FrequencyUnit.Day, 2)]
    [InlineData(HabitSchedule.Recurring, FrequencyUnit.Week, 1)]
    [InlineData(HabitSchedule.Flexible, FrequencyUnit.Week, 3)]
    [InlineData(HabitSchedule.OneTime, null, null)]
    [InlineData(HabitSchedule.General, null, null)]
    public void EverySchedule_AcceptsRepeatOnCreateAndUpdate(HabitSchedule schedule, FrequencyUnit? unit, int? quantity)
    {
        var parameters = Parameters(new(480)) with { Schedule = schedule, FrequencyUnit = unit, FrequencyQuantity = quantity };
        var result = Habit.Create(parameters);
        result.IsSuccess.Should().BeTrue();
        var habit = Habit.Create(parameters with { RepeatReminder = null }).Value;
        habit.Update(new HabitUpdateParams(schedule, habit.Title, null, unit, quantity, [], false,
            habit.NextDueDate, RepeatReminder: new(30))).IsSuccess.Should().BeTrue();
        habit.RepeatReminder!.IntervalMinutes.Should().Be(30);
    }

    [Fact]
    public void WeekdayAndIntervalGates_AcceptRepeat() =>
        Habit.Create(Parameters(new(30)) with { Days = [DayOfWeek.Monday], IntervalWeeks = 2 }).IsSuccess.Should().BeTrue();

    [Fact]
    public void Repeat_ReplacesOtherReminders()
    {
        var habit = Habit.Create(Parameters(new(30)) with
        {
            DueTime = new(8, 0),
            ReminderTimes = [15],
            ScheduledReminders = [new(ScheduledReminderWhen.SameDay, new(8, 0))]
        }).Value;
        habit.ReminderTimes.Should().BeEmpty();
        habit.ScheduledReminders.Should().BeEmpty();
        habit.RelativeReminders.Should().BeEmpty();
    }

    [Fact]
    public void ActiveWindow_MayCrossMidnight()
    {
        Habit.Create(Parameters(new(480, new(22, 0), new(6, 0)))).IsSuccess.Should().BeTrue();
        Habit.Create(Parameters(new(480, new(22, 0)))).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void FlexibleRepeat_AllowsAnotherDoseAfterDailyTarget()
    {
        var parameters = Parameters(new(480)) with { Schedule = HabitSchedule.Flexible };
        var habit = Habit.Create(parameters).Value;
        habit.Log(parameters.DueDate).IsSuccess.Should().BeTrue();
        habit.Log(parameters.DueDate).IsSuccess.Should().BeTrue();
        habit.Logs.Select(log => log.CompletionOrdinal).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Recurring_StillStopsAfterOneCompletion()
    {
        var habit = Habit.Create(Parameters(new(480))).Value;
        habit.Log(new(2027, 9, 26)).IsSuccess.Should().BeTrue();
        habit.Log(new(2027, 9, 26)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void DoseInstant_CanBeCorrectedButCannotBeInTheFuture()
    {
        var habit = Habit.Create(Parameters(new(480))).Value;
        var log = habit.Log(new(2027, 9, 26)).Value;
        var nowAtUtc = new DateTime(2027, 9, 26, 12, 30, 0, DateTimeKind.Utc);
        habit.CorrectDose(log.Id, nowAtUtc.AddMinutes(-30), nowAtUtc).IsSuccess.Should().BeTrue();
        log.TakenAtUtc.Should().Be(nowAtUtc.AddMinutes(-30));
        habit.CorrectDose(log.Id, nowAtUtc.AddMinutes(1), nowAtUtc).IsFailure.Should().BeTrue();
        habit.CorrectDose(log.Id, DateTime.SpecifyKind(nowAtUtc, DateTimeKind.Unspecified), nowAtUtc)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void FlexiblePerDay_AllowsAnyDailyTargetWithoutDerivingItFromTheReminder()
    {
        var habit = Habit.Create(Parameters(new(30)) with { Schedule = HabitSchedule.Flexible, FrequencyQuantity = 3 }).Value;
        habit.FrequencyQuantity.Should().Be(3);
        for (var count = 0; count < 4; count++)
            habit.Log(new(2027, 9, 26)).IsSuccess.Should().BeTrue();
        habit.FrequencyQuantity.Should().Be(3);
    }

    [Fact]
    public void Update_PreservesRepeatAcrossScheduleChangesAndCanClearIt()
    {
        var habit = Habit.Create(Parameters(new(30))).Value;
        var update = new HabitUpdateParams(habit.Schedule, habit.Title, null, FrequencyUnit.Day, 1,
            [DayOfWeek.Monday], false, habit.NextDueDate);
        habit.Update(update).IsSuccess.Should().BeTrue();
        habit.RepeatReminder!.IntervalMinutes.Should().Be(30);
        habit.Update(update with { ClearRepeatReminder = true, ScheduledReminders = [new(ScheduledReminderWhen.SameDay, new(8, 0))] })
            .IsSuccess.Should().BeTrue();
        habit.RepeatReminder.Should().BeNull();
        habit.ScheduledReminders.Should().ContainSingle();
    }

    [Fact]
    public void DefaultStartAndDoseInstants_AreSetWhenSaved()
    {
        var beforeAtUtc = DateTime.UtcNow;
        var habit = Habit.Create(Parameters(new(30))).Value;
        var log = habit.Log(new(2027, 9, 26)).Value;
        habit.RepeatStartAtUtc.Should().BeOnOrAfter(beforeAtUtc).And.BeOnOrBefore(DateTime.UtcNow);
        log.TakenAtUtc.Should().Be(log.CreatedAtUtc);
    }

    private static HabitCreateParams Parameters(RepeatReminderOptions repeat) => new(
        HabitSchedule.Recurring, Guid.NewGuid(), "Drink water", FrequencyUnit.Day, 1,
        new(2027, 9, 26), ReminderEnabled: true, RepeatReminder: repeat);
}
