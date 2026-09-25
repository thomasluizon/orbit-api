using FluentAssertions;
using Orbit.Application.Chat;
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

    [Fact]
    public void HistoricalOverdueCompletionOnOffCadenceDay_CountsInDayAndPeriod()
    {
        var today = new DateOnly(2026, 4, 3);
        var anchor = today.AddDays(-1);
        var habit = Habit.Create(new HabitCreateParams(UserId, "Read", FrequencyUnit.Day, 2, anchor)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
            habit, new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc));
        habit.Log(today).IsSuccess.Should().BeTrue();
        var zone = TimeZoneInfo.Utc;

        var day = RetrospectiveMetricsCalculator.ComputeHistorical([habit], today, today, 0, 0, zone);
        day.TotalScheduled.Should().Be(1);
        day.CompletionRate.Should().Be(100);
        day.CompletionSeries!.Points.Single().Should().Be(
            new CompletionSeriesPoint(today, today, 1, 1, 100));

        var period = RetrospectiveMetricsCalculator.ComputeHistorical(
            [habit], today.AddDays(-29), today, 0, 0, zone);
        period.TotalScheduled.Should().Be(2);
        period.CompletionRate.Should().Be(50);
        period.CompletionSeries!.Points[^1].Should().Be(
            new CompletionSeriesPoint(today, today, 1, 1, 100));
        period.CompletionSeries.Points.Sum(point => point.Scheduled).Should().Be(period.TotalScheduled);
        period.CompletionSeries.Points.Sum(point => point.Completed).Should().Be(1);
    }

    [Fact]
    public void LiveRetrospectiveAndMetricsCard_CreditOffCadenceLog()
    {
        var today = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Read", FrequencyUnit.Day, 2, today.AddDays(-1))).Value;
        habit.Log(today).IsSuccess.Should().BeTrue();

        var metrics = RetrospectiveMetricsCalculator.Compute([habit], today, today, 0, 0);
        var card = MetricsCardBuilder.Build("week", metrics);

        metrics.TotalScheduled.Should().Be(1);
        metrics.CompletionRate.Should().Be(100);
        metrics.CompletionSeries!.Points.Single().Should().Be(
            new CompletionSeriesPoint(today, today, 1, 1, 100));
        card.TotalScheduled.Should().Be(1);
        card.Series!.Points.Single().Completed.Should().Be(1);
    }

    [Fact]
    public void UnvalidatedExtraLogs_DoNotCreditMissedOccurrences()
    {
        var from = new DateOnly(2026, 4, 1);
        var habit = Habit.Create(new HabitCreateParams(UserId, "Weekly", FrequencyUnit.Day, 2, from)).Value;
        habit.Log(from, advanceDueDate: false);
        habit.Log(from.AddDays(1), advanceDueDate: false);

        var metrics = RetrospectiveMetricsCalculator.Compute([habit], from, from.AddDays(2), 0, 0);

        metrics.TotalScheduled.Should().Be(2);
        metrics.CompletionRate.Should().Be(50);
        metrics.CompletionSeries!.Points.Sum(point => point.Completed).Should().Be(1);
        metrics.TopHabits.Single().CompletionRate.Should().Be(50);
    }

    private static Habit Daily(string title, DateOnly dueDate) =>
        Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, dueDate)).Value;
}
