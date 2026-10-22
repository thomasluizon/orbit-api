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
    public void CountsKeepRecordsDatesAndScheduleSeparate()
    {
        var day = new DateOnly(2026, 4, 1);
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Read", null, null, day)).Value;
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
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Read", null, null, day)).Value;
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
