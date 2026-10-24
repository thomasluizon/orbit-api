using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Services;

public class HabitScheduleEnumReaderTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);

    [Theory]
    [InlineData(HabitSchedule.OneTime)]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public void ScheduleReaders_UsePersistedScheduleAfterSnapshotRoundTrip(HabitSchedule schedule)
    {
        var habit = Create(schedule);
        var divergent = Rehydrate(habit);
        var end = Start.AddDays(3);

        HabitScheduleService.GetScheduledDates(divergent, Start, end)
            .Should().Equal(HabitScheduleService.GetScheduledDates(habit, Start, end));
        HabitScheduleService.GetHistoricalScheduledDates(divergent, Start, end, TimeZoneInfo.Utc)
            .Should().Equal(HabitScheduleService.GetHistoricalScheduledDates(habit, Start, end, TimeZoneInfo.Utc));
        HabitScheduleService.GetUnionScheduledDates([divergent], Start, end)
            .Should().BeEquivalentTo(HabitScheduleService.GetUnionScheduledDates([habit], Start, end));
        HabitScheduleService.GetUnionScheduledDatesForStreak([divergent], Start, end, TimeZoneInfo.Utc)
            .Should().BeEquivalentTo(HabitScheduleService.GetUnionScheduledDatesForStreak([habit], Start, end, TimeZoneInfo.Utc));
        HabitScheduleService.IsFlexibleHabitDueOnDate(divergent, Start, divergent.Logs, 1)
            .Should().Be(HabitScheduleService.IsFlexibleHabitDueOnDate(habit, Start, habit.Logs, 1));
        HabitScheduleService.GetInstances(divergent, Start, end, Start)
            .Should().Equal(HabitScheduleService.GetInstances(habit, Start, end, Start));
    }

    [Fact]
    public void FlexibleMetrics_UsePersistedScheduleAfterSnapshotRoundTrip()
    {
        var habit = Create(HabitSchedule.Flexible);
        habit.Log(Start.AddDays(2), advanceDueDate: false).IsSuccess.Should().BeTrue();
        var divergent = Rehydrate(habit);
        divergent.LoadScheduleLogsForRead(habit.Logs);

        HabitMetricsCalculator.Calculate(divergent, Start.AddDays(2), 1, TimeZoneInfo.Utc)
            .Should().Be(HabitMetricsCalculator.Calculate(habit, Start.AddDays(2), 1, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime)]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public void SnapshotProducers_PreservePersistedSchedule(HabitSchedule schedule)
    {
        var habit = Rehydrate(Create(schedule));
        var query = new[] { habit }.AsQueryable();
        var snapshots = new[]
        {
            HabitScheduleSnapshot.FromHabit(habit),
            HabitScheduleProjection.Select(query).Single(),
            HabitScheduleProjection.SelectForFreeze(query).Single().Snapshot
        };

        foreach (var snapshot in snapshots)
        {
            snapshot.Schedule.Should().Be(schedule);
            snapshot.StartDate.Should().Be(Start);
            snapshot.NextDueDate.Should().Be(habit.NextDueDate);
        }
    }

    private static Habit Create(HabitSchedule schedule) => Habit.Create(new HabitCreateParams(schedule,
        Guid.NewGuid(), "Habit",
        schedule == HabitSchedule.Flexible ? FrequencyUnit.Week
            : schedule == HabitSchedule.Recurring ? FrequencyUnit.Day : null,
        schedule == HabitSchedule.Flexible ? 2
            : schedule == HabitSchedule.Recurring ? 1 : null,
        Start,
        CreatedAtUtc: Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;

    private static Habit Rehydrate(Habit habit) => Habit.FromScheduleSnapshot(
        HabitScheduleSnapshot.FromHabit(habit), habit.UserId);
}
