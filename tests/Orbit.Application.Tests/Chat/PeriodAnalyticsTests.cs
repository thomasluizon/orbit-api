using Orbit.Domain.Enums;
using NSubstitute;
using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Chat;

public sealed class PeriodAnalyticsTests
{
    [Fact]
    public void ThreeHabitsWithFiveLogsReturnThreeHabitsAndFiveCompletions()
    {
        var from = new DateOnly(2026, 4, 1);
        var user = Guid.NewGuid();
        var habits = Enumerable.Range(0, 3).Select(index => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, user, $"Habit {index}", FrequencyUnit.Day, 1, DueDate: from)).Value).ToList();
        foreach (var habit in habits)
            habit.Log(from, advanceDueDate: false).IsSuccess.Should().BeTrue();
        habits[0].Log(from.AddDays(1), advanceDueDate: false);
        habits[1].Log(from.AddDays(2), advanceDueDate: false);

        var (metrics, _) = PeriodAnalyticsCalculator.Calculate(habits, new(from, from.AddDays(6)), TimeZoneInfo.Utc, 1);

        metrics.PositiveLogRecords.Should().Be(5);
        PeriodAnalyticsCalculator.Value(metrics, "completedHabits").Should().Be(3);
    }

    [Fact]
    public void CompletedHabitsExcludeTasksSlipsDeletedUndoneAndOutsideWindow()
    {
        var from = new DateOnly(2026, 4, 1);
        var user = Guid.NewGuid();
        Habit Create(string title, HabitSchedule schedule = HabitSchedule.Recurring, bool bad = false) =>
            Habit.Create(new HabitCreateParams(schedule, user, title,
                schedule == HabitSchedule.OneTime ? null : FrequencyUnit.Day,
                schedule == HabitSchedule.OneTime ? null : 1, DueDate: from, IsBadHabit: bad)).Value;
        var parent = Create("Parent");
        var child = Create("Child");
        child.SetParentHabitId(parent.Id);
        child.Log(from, advanceDueDate: false);
        var flexible = Create("Flexible", HabitSchedule.Flexible);
        flexible.Log(from, advanceDueDate: false);
        var ended = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user, "Ended", FrequencyUnit.Day, 1,
            DueDate: from, EndDate: from)).Value;
        ended.Log(from);
        var task = Create("Task", HabitSchedule.OneTime);
        task.Log(from);
        var bad = Create("Smoking", bad: true);
        bad.Log(from, advanceDueDate: false);
        var deleted = Create("Deleted");
        deleted.Log(from, advanceDueDate: false);
        deleted.SoftDelete();
        var undone = Create("Undone");
        undone.Log(from, advanceDueDate: false);
        undone.Unlog(from);
        var outside = Create("Outside");
        outside.Log(from.AddDays(-1), advanceDueDate: false);
        outside.Log(from.AddDays(7), advanceDueDate: false);
        var deletedLog = Create("Deleted log");
        deletedLog.Log(from, advanceDueDate: false).Value.SoftDelete();
        var skipped = Create("Skipped", HabitSchedule.Flexible);
        skipped.SkipFlexible(from);

        var (metrics, _) = PeriodAnalyticsCalculator.Calculate(
            [parent, child, child, flexible, ended, task, bad, deleted, undone, outside, deletedLog, skipped],
            new(from, from.AddDays(6)), TimeZoneInfo.Utc, 1);

        PeriodAnalyticsCalculator.Value(metrics, "completedHabits").Should().Be(3);
    }

    [Theory]
    [InlineData("completedHabits", 3, "habits")]
    [InlineData("positiveLogRecords", 3, "records")]
    public async Task QueryCountsIndependentChildrenWithoutChangingLegacyTotals(string metric, int value, string unit)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        var from = new DateOnly(2026, 4, 1);
        Habit Create(string title) => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, title, FrequencyUnit.Day, 1, DueDate: from)).Value;
        var parent = Create("Parent");
        var child = Create("Child");
        child.SetParentHabitId(parent.Id);
        var other = Create("Other");
        parent.Log(from, advanceDueDate: false);
        parent.Log(from.AddDays(1), advanceDueDate: false);
        child.Log(from, advanceDueDate: false);
        child.Log(from.AddDays(1), advanceDueDate: false);
        other.Log(from.AddDays(6), advanceDueDate: false);
        other.Log(from.AddDays(-1), advanceDueDate: false);
        var habits = Substitute.For<Orbit.Domain.Interfaces.IGenericRepository<Habit>>();
        var users = Substitute.For<Orbit.Domain.Interfaces.IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        habits.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns([parent, child, other]);
        var handler = new GetPeriodAnalyticsQueryHandler(habits, users);

        var result = await handler.Handle(new(user.Id, from, from.AddDays(6), metric,
            BaselineFrom: from.AddDays(-7), BaselineTo: from.AddDays(-1)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Total.Value.Should().Be(value);
        result.Value.Total.Unit.Should().Be(unit);
        result.Value.Total.Metrics.CompletedHabits.Should().Be(3);
        result.Value.Total.Metrics.PositiveLogRecords.Should().Be(3);
        var comparison = result.Value.Comparison!;
        comparison.Current.CompletedHabits.Should().Be(3);
        comparison.Baseline.CompletedHabits.Should().Be(1);
        comparison.CurrentValue.Should().Be(value);
        comparison.BaselineValue.Should().Be(1);
        comparison.AbsoluteDelta.Should().Be(2);
        comparison.RelativeDelta.Should().Be(2);
        var filtered = await handler.Handle(new(user.Id, from, from.AddDays(6), "completedHabits", Search: "Child"), default);
        filtered.Value.Total.Value.Should().Be(1);
        var empty = await handler.Handle(new(user.Id, from, from.AddDays(6), "completedHabits", Search: "Missing"), default);
        empty.Value.Total.Value.Should().Be(0);
        new GetPeriodAnalyticsQueryValidator().Validate(new GetPeriodAnalyticsQuery(user.Id, from, from.AddDays(6), "completedHabits"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void CountsKeepRecordsDatesAndScheduleSeparate()
    {
        var day = new DateOnly(2026, 4, 1);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, Guid.NewGuid(), "Read", null, null, day)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(habit, day.AddDays(-1).ToDateTime(TimeOnly.MinValue));
        habit.LoadScheduleLogsForRead([
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, day, 1, 1, DateTime.UnixEpoch),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, day, 1, 2, DateTime.UnixEpoch),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, day.AddDays(1), 1, 1, DateTime.UnixEpoch)]);
        var (metrics, series) = PeriodAnalyticsCalculator.Calculate([habit], new(day, day.AddDays(1)), TimeZoneInfo.Utc, 1);
        metrics.PositiveLogRecords.Should().Be(3);
        metrics.DistinctLoggedDates.Should().Be(2);
        metrics.ScheduledDates.Should().Be(1);
        metrics.MetScheduledDates.Should().Be(1);
        series.Points.Sum(p => p.Completed).Should().Be(metrics.MetScheduledDates);
        metrics.CurrentStreak.Should().Be(2);
    }

    [Fact]
    public void NoScheduleKeepsRateNullAndLongWindowsUseWeeks()
    {
        var (metrics, series) = PeriodAnalyticsCalculator.Calculate([], new(new(2026, 1, 1), new(2026, 3, 31)), TimeZoneInfo.Utc, 1);
        metrics.CompletionRate.Should().BeNull();
        series.Granularity.Should().Be("week");
        series.Points.First().StartDate.Should().Be(metrics.Window.From);
        series.Points.Last().EndDate.Should().Be(metrics.Window.To);
        series.Points.Should().OnlyContain(p => p.CompletionRate == null);
    }

    [Fact]
    public void RejectsInvalidWindowsAndMetrics()
    {
        var validator = new GetPeriodAnalyticsQueryValidator();
        validator.Validate(new GetPeriodAnalyticsQuery(Guid.NewGuid(), new(2026, 4, 2), new(2026, 4, 1))).IsValid.Should().BeFalse();
        validator.Validate(new GetPeriodAnalyticsQuery(Guid.NewGuid(), new(2026, 4, 1), new(2026, 4, 2), "minutes")).IsValid.Should().BeFalse();
        validator.Validate(new GetPeriodAnalyticsQuery(Guid.NewGuid(), new(2026, 4, 1), new(2026, 4, 2), BaselineFrom: new(2026, 3, 1))).IsValid.Should().BeFalse();
        var act = () => new ReadWindow(new(2026, 4, 2), new(2026, 4, 1));
        act.Should().Throw<ArgumentException>();
    }
    [Theory]
    [InlineData("positiveLogRecords", 1, 2, -1, -0.5)]
    [InlineData("distinctLoggedDates", 1, 1, 0, 0)]
    public async Task ComparisonComesFromActualLogsAndPreservesMetricUnits(string metric, int current, int baseline,
        int delta, decimal relative)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        var day = new DateOnly(2026, 4, 1);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, "Read", null, null, day)).Value;
        habit.LoadScheduleLogsForRead([
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, day, 1, 1, DateTime.UnixEpoch),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, day, 1, 2, DateTime.UnixEpoch),
            HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, day.AddDays(1), 1, 1, DateTime.UnixEpoch)]);
        var habits = NSubstitute.Substitute.For<Orbit.Domain.Interfaces.IGenericRepository<Habit>>();
        var users = NSubstitute.Substitute.For<Orbit.Domain.Interfaces.IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, NSubstitute.Arg.Any<CancellationToken>()).Returns(user);
        habits.FindAsync(NSubstitute.Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            NSubstitute.Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), NSubstitute.Arg.Any<CancellationToken>()).Returns([habit]);
        var handler = new GetPeriodAnalyticsQueryHandler(habits, users);
        var result = await handler.Handle(new(user.Id, day.AddDays(1), day.AddDays(1), metric,
            BaselineFrom: day, BaselineTo: day), default);
        result.IsSuccess.Should().BeTrue();
        var comparison = result.Value.Comparison!;
        comparison.CurrentValue.Should().Be(current);
        comparison.BaselineValue.Should().Be(baseline);
        comparison.AbsoluteDelta.Should().Be(delta);
        comparison.RelativeDelta.Should().Be(relative);
        comparison.Unit.Should().Be(metric == "positiveLogRecords" ? "records" : "days");
        var zero = await handler.Handle(new(user.Id, day, day, metric,
            BaselineFrom: day.AddDays(-1), BaselineTo: day.AddDays(-1)), default);
        zero.Value.Comparison!.RelativeDelta.Should().BeNull();
    }

}
