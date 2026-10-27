using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class MetricsFixtureScopeTests
{
    [Fact]
    public void BackdatedFixtureLogsDoNotEstablishAHistoricalScheduleBeforeCreation()
    {
        var today = new DateOnly(2026, 10, 27);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, Guid.NewGuid(), "Beber água",
            FrequencyUnit.Day, 1, DueDate: today.AddDays(-6),
            CreatedAtUtc: today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        foreach (var offset in new[] { -6, -4, -3, -2, -1 })
            habit.Log(today.AddDays(offset)).IsSuccess.Should().BeTrue();
        habit.Update(new(HabitSchedule.Recurring, habit.Title, null, FrequencyUnit.Day, 1, [], false, today))
            .IsSuccess.Should().BeTrue();

        var (metrics, chart) = PeriodAnalyticsCalculator.Calculate([habit],
            new ReadWindow(today.AddDays(-29), today), TimeZoneInfo.Utc, 1);

        metrics.PositiveLogRecords.Should().Be(5);
        metrics.DistinctLoggedDates.Should().Be(5);
        metrics.ScheduledDates.Should().Be(1);
        metrics.MetScheduledDates.Should().Be(0);
        chart.Points.Sum(point => point.Completed).Should().Be(metrics.MetScheduledDates);
        chart.Points.Sum(point => point.Scheduled).Should().Be(metrics.ScheduledDates);
        chart.Points.Where(point => point.EndDate < today).Should().OnlyContain(point => point.CompletionRate == null);
    }
}
