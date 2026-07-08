using FluentAssertions;
using FsCheck.Xunit;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Tests.Generators;

namespace Orbit.Application.Tests.Services;

[Properties(Arbitrary = new[] { typeof(OrbitArbitraries) }, MaxTest = 100, Replay = "(40000003,40000037)")]
public class HabitSchedulePropertyTests
{
    private static int TrueMod(int value, int modulus) => ((value % modulus) + modulus) % modulus;

    private static HashSet<DateOnly> ToDateSet(DateOnly from, int span, int[] offsets) =>
        offsets.Select(offset => from.AddDays(TrueMod(offset, span + 1))).ToHashSet();

    [Property]
    public void Generators_ProduceValidHabitShapes(Habit any, RecurringHabit recurring, FlexibleHabit flexible, BadHabit bad)
    {
        any.Title.Should().NotBeNullOrWhiteSpace();
        recurring.Habit.FrequencyUnit.Should().NotBeNull();
        recurring.Habit.IsFlexible.Should().BeFalse();
        recurring.Habit.IsBadHabit.Should().BeFalse();
        flexible.Habit.IsFlexible.Should().BeTrue();
        flexible.Habit.FrequencyUnit.Should().NotBeNull();
        bad.Habit.IsBadHabit.Should().BeTrue();
    }

    [Property]
    public void RecurringHabit_IsNeverOverdue_OnOrBeforeItsDueDate(RecurringHabit recurring, int rawOffset)
    {
        var habit = recurring.Habit;
        var referenceDate = habit.DueDate.AddDays(-Math.Abs(rawOffset % 401));

        HabitScheduleService.IsOverdueOnDate(habit, referenceDate).Should().BeFalse();
    }

    [Property]
    public void RecurringHabit_MissedPastOccurrence_IffDueDateBeforeReference(RecurringHabit recurring, int rawOffset)
    {
        var habit = recurring.Habit;
        var reference = habit.DueDate.AddDays(rawOffset % 401);

        HabitScheduleService.HasMissedPastOccurrence(habit, reference)
            .Should().Be(habit.DueDate < reference);
    }

    [Property]
    public void FlexibleHabit_IsNeverOverdue(FlexibleHabit flexible, DateOnly reference)
    {
        HabitScheduleService.IsOverdueOnDate(flexible.Habit, reference).Should().BeFalse();
    }

    [Property]
    public void BadHabit_IsNeverOverdueOrMissed(BadHabit bad, DateOnly reference)
    {
        HabitScheduleService.IsOverdueOnDate(bad.Habit, reference).Should().BeFalse();
        HabitScheduleService.HasMissedPastOccurrence(bad.Habit, reference).Should().BeFalse();
    }

    [Property]
    public void GetScheduledDates_EqualsIndependentDueDayScan(Habit habit, DateOnly from, RunLength span)
    {
        var to = from.AddDays(span.Value);
        var expected = new List<DateOnly>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (HabitScheduleService.IsHabitDueOnDate(habit, date))
                expected.Add(date);
        }

        HabitScheduleService.GetScheduledDates(habit, from, to).Should().Equal(expected);
    }

    [Property]
    public void CompletedAndSkippedWindows_PartitionInWindowLogs(FlexibleHabit flexible, int[] rawOffsets, int rawWeekStart)
    {
        var habit = flexible.Habit;
        var weekStartDay = Math.Abs(rawWeekStart % 2);
        var target = habit.DueDate;

        foreach (var raw in rawOffsets)
        {
            var date = target.AddDays(raw % 11);
            if (raw % 2 == 0) habit.Log(date);
            else habit.SkipFlexible(date);
        }

        var start = HabitScheduleService.GetWindowStart(habit, target, weekStartDay);
        var end = HabitScheduleService.GetWindowEnd(habit, target, weekStartDay);
        var inWindow = habit.Logs.Count(l => l.Date >= start && l.Date <= end);
        var completedManual = habit.Logs.Count(l => l.Date >= start && l.Date <= end && l.Value > 0);
        var skippedManual = habit.Logs.Count(l => l.Date >= start && l.Date <= end && l.Value == 0);

        HabitScheduleService.GetCompletedInWindow(habit, target, habit.Logs, weekStartDay).Should().Be(completedManual);
        HabitScheduleService.GetSkippedInWindow(habit, target, habit.Logs, weekStartDay).Should().Be(skippedManual);
        (completedManual + skippedManual).Should().Be(inWindow);
    }

    [Property]
    public void FullyCompletedRun_IncrementsStreakByOnePerDay(DateOnly from, RunLength span)
    {
        var to = from.AddDays(span.Value);
        var days = Enumerable.Range(0, span.Value + 1).Select(from.AddDays).ToHashSet();
        var noFreezes = new HashSet<DateOnly>();

        var result = HabitScheduleService.ComputeStreakAsOf(days, days, noFreezes, from, to);

        result.Streak.Should().Be(span.Value + 1);
    }

    [Property]
    public void ScheduledFreeze_BridgesStreakWithoutIncrementing(DateOnly from, RunLength span)
    {
        var to = from.AddDays(span.Value);
        var allDays = Enumerable.Range(0, span.Value + 1).Select(from.AddDays).ToHashSet();
        var freezeDay = from.AddDays(span.Value / 2);
        var completed = allDays.Where(day => day != freezeDay).ToHashSet();
        var freezes = new HashSet<DateOnly> { freezeDay };

        var result = HabitScheduleService.ComputeStreakAsOf(allDays, completed, freezes, from, to);

        result.Streak.Should().Be(span.Value);
    }

    [Property]
    public void StreakEngines_AgreeAtAnchor(DateOnly from, RunLength span, int[] expectedOffsets, int[] completedOffsets, int[] freezeOffsets)
    {
        var to = from.AddDays(span.Value);
        var expected = ToDateSet(from, span.Value, expectedOffsets);
        var completed = ToDateSet(from, span.Value, completedOffsets);
        var freezes = ToDateSet(from, span.Value, freezeOffsets);

        var asOf = HabitScheduleService.ComputeStreakAsOf(expected, completed, freezes, from, to);
        var series = HabitScheduleService.BuildStreakSeries(expected, completed, freezes, from, from, to);

        series.Should().NotBeEmpty();
        series[^1].Streak.Should().Be(asOf.Streak);
        asOf.Streak.Should().BeGreaterThanOrEqualTo(0);
    }
}
