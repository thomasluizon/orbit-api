using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.Tests.Entities;

public class RepeatReminderTests
{
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
    public void NonDailySchedule_RejectsRepeat(HabitSchedule schedule, FrequencyUnit? unit, int? quantity) =>
        Habit.Create(Parameters(new(480)) with { Schedule = schedule, FrequencyUnit = unit, FrequencyQuantity = quantity })
            .IsFailure.Should().BeTrue();

    [Fact]
    public void WeekdayGate_RejectsRepeat() =>
        Habit.Create(Parameters(new(30)) with { Days = [DayOfWeek.Monday] }).IsFailure.Should().BeTrue();

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

    [Theory]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public void Repeat_AllowsAnotherDoseAfterDailyTarget(HabitSchedule schedule)
    {
        var parameters = Parameters(new(480)) with { Schedule = schedule };
        if (schedule == HabitSchedule.General)
            parameters = parameters with { FrequencyUnit = null, FrequencyQuantity = null };
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

    private static HabitCreateParams Parameters(RepeatReminderOptions repeat) => new(
        HabitSchedule.Recurring, Guid.NewGuid(), "Drink water", FrequencyUnit.Day, 1,
        new(2027, 9, 26), ReminderEnabled: true, RepeatReminder: repeat);
}
