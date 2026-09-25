using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Services;

public class RetrospectiveCompletionSeriesTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void SevenDays_CountsScheduledOccurrencesAndLeavesEmptyRatesNull()
    {
        var from = new DateOnly(2026, 9, 1);
        var first = Daily("Read", from);
        var second = Daily("Walk", from);
        first.Log(from.AddDays(2), advanceDueDate: false);

        var series = RetrospectiveMetricsCalculator.Compute([first, second], from, from.AddDays(6), 0, 0)
            .CompletionSeries!;

        series.Granularity.Should().Be("day");
        series.Points.Should().HaveCount(7);
        series.Points[2].Should().Be(new CompletionSeriesPoint(from.AddDays(2), from.AddDays(2), 2, 1, 50));
        series.Points[0].Should().Be(new CompletionSeriesPoint(from, from, 2, 0, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Year_UsesAlignedPartialWeeksAndConservesScheduledTotal(int weekStartDay)
    {
        var from = new DateOnly(2025, 9, 26);
        var to = from.AddDays(364);
        var daily = Daily("Read", from);

        var metrics = RetrospectiveMetricsCalculator.Compute([daily], from, to, 0, 0, weekStartDay);
        var series = metrics.CompletionSeries!;

        series.Granularity.Should().Be("week");
        series.Points.Count.Should().BeInRange(52, 53);
        series.Points[0].StartDate.Should().Be(from);
        series.Points[0].EndDate.Should().Be(weekStartDay == 0
            ? new DateOnly(2025, 9, 27)
            : new DateOnly(2025, 9, 28));
        series.Points[^1].EndDate.Should().Be(to);
        series.Points.Sum(point => point.Scheduled).Should().Be(metrics.TotalScheduled);
    }

    [Fact]
    public void ExcludesBadHabitsAndMarksUnscheduledDayNull()
    {
        var from = new DateOnly(2026, 9, 1);
        var oneTime = Habit.Create(new HabitCreateParams(UserId, "Once", null, null, from.AddDays(2))).Value;
        var bad = Habit.Create(new HabitCreateParams(UserId, "Slip", FrequencyUnit.Day, 1, from, IsBadHabit: true)).Value;
        bad.Log(from.AddDays(2), advanceDueDate: false);

        var series = RetrospectiveMetricsCalculator.Compute([oneTime, bad], from, from.AddDays(6), 0, 0)
            .CompletionSeries!;

        series.Points[0].CompletionRate.Should().BeNull();
        series.Points[2].Scheduled.Should().Be(1);
        series.Points[2].Completed.Should().Be(0);
    }

    [Fact]
    public void HistoricalWindowAcrossDst_HasOnePointPerLocalDay()
    {
        var from = new DateOnly(2026, 3, 5);
        var habit = Daily("Read", from);
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
            habit, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        habit.Log(new DateOnly(2026, 3, 8), advanceDueDate: false);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var series = RetrospectiveMetricsCalculator.ComputeHistorical(
            [habit], from, from.AddDays(6), 0, 0, zone).CompletionSeries!;

        series.Points.Should().HaveCount(7);
        series.Points[3].StartDate.Should().Be(new DateOnly(2026, 3, 8));
        series.Points[3].Completed.Should().Be(1);
    }

    private static Habit Daily(string title, DateOnly dueDate) =>
        Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, dueDate)).Value;
}
