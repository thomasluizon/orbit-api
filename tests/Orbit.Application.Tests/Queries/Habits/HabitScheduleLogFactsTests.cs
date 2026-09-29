using FluentAssertions;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Habits;

public class HabitScheduleLogFactsTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly DueDate = new(2026, 6, 1);

    private static Habit CreateHabit(bool isFlexible = true, int quantity = 3, int? intervalWeeks = null) =>
        Habit.Create(new HabitCreateParams(
            UserId, "Weekly habit", FrequencyUnit.Week, quantity, DueDate,
            IsFlexible: isFlexible, IntervalWeeks: intervalWeeks)).Value;

    [Fact]
    public void ResolvedDates_ContainsCompletedAndSkippedDaysOnlyForTheRequestedHabit()
    {
        var habitId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var facts = new HabitScheduleLogFacts([
            new(habitId, DueDate, 1, 0, true),
            new(habitId, DueDate.AddDays(1), 0, 1, true),
            new(habitId, DueDate.AddDays(2), 0, 0, true),
            new(otherId, DueDate.AddDays(3), 1, 0, true)]);

        facts.ResolvedDates(habitId).Should().BeEquivalentTo([DueDate, DueDate.AddDays(1)]);
        facts.ResolvedDates(Guid.NewGuid()).Should().BeEmpty();
    }

    [Fact]
    public void HasCompletedAndHasLog_IncludeBothRangeEdgesAndRespectLogKinds()
    {
        var habitId = Guid.NewGuid();
        var from = DueDate;
        var to = DueDate.AddDays(2);
        var facts = new HabitScheduleLogFacts([
            new(habitId, from.AddDays(-1), 1, 0, true),
            new(habitId, from, 0, 1, true),
            new(habitId, to, 1, 0, true),
            new(habitId, to.AddDays(1), 1, 0, true),
            new(Guid.NewGuid(), from.AddDays(1), 1, 0, true)]);

        facts.HasCompleted(habitId, from, from).Should().BeFalse();
        facts.HasLog(habitId, from, from).Should().BeTrue();
        facts.HasCompleted(habitId, to, to).Should().BeTrue();
        facts.HasLog(habitId, to, to).Should().BeTrue();
        facts.HasCompleted(habitId, from.AddDays(1), from.AddDays(1)).Should().BeFalse();
        facts.HasLog(habitId, from.AddDays(1), from.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void IsFlexibleDue_RejectsNonFlexibleEarlyAndInactiveWeeks()
    {
        var ordinary = CreateHabit(isFlexible: false);
        var flexible = CreateHabit(intervalWeeks: 2);
        var facts = new HabitScheduleLogFacts([]);

        facts.IsFlexibleDue(ordinary, DueDate, 1).Should().BeFalse();
        facts.IsFlexibleDue(flexible, DueDate.AddDays(-1), 1).Should().BeFalse();
        facts.IsFlexibleDue(flexible, DueDate.AddDays(9), 1).Should().BeFalse();
        facts.IsFlexibleDue(flexible, DueDate.AddDays(2), 1).Should().BeTrue();
    }

    [Fact]
    public void IsFlexibleDue_CountsOnlyTheCurrentWindowAndReducesTargetForSkips()
    {
        var habit = CreateHabit();
        var facts = new HabitScheduleLogFacts([
            new(habit.Id, DueDate.AddDays(-1), 10, 0, true),
            new(habit.Id, DueDate, 1, 0, true),
            new(habit.Id, DueDate.AddDays(1), 0, 1, true),
            new(habit.Id, DueDate.AddDays(7), 10, 0, true)]);

        facts.IsFlexibleDue(habit, DueDate.AddDays(2), 1).Should().BeTrue();
        var metFacts = new HabitScheduleLogFacts([
            new(habit.Id, DueDate, 2, 0, true),
            new(habit.Id, DueDate.AddDays(1), 0, 1, true)]);
        metFacts.IsFlexibleDue(habit, DueDate.AddDays(2), 1).Should().BeFalse();
        var skippedFacts = new HabitScheduleLogFacts([
            new(habit.Id, DueDate, 0, 3, true)]);
        skippedFacts.IsFlexibleDue(habit, DueDate.AddDays(2), 1).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void GetVisibleScheduledDates_UsesPositiveCompletionWhenSkipSharesDate(
        bool complete, bool skip, bool visible)
    {
        var habit = CreateHabit();
        if (complete)
            habit.Log(DueDate).IsSuccess.Should().BeTrue();
        if (skip)
            habit.SkipFlexible(DueDate).IsSuccess.Should().BeTrue();
        var facts = new HabitScheduleLogFacts([
            new HabitScheduleLogDay(
                habit.Id, DueDate,
                habit.Logs.Count(log => !log.IsDeleted && log.Value > 0),
                habit.Logs.Count(log => !log.IsDeleted && log.Value == 0),
                true)]);

        foreach (var source in new HabitScheduleLogFacts?[] { facts, null })
        {
            var dates = HabitScheduleFilters.GetVisibleScheduledDates(
                habit, DueDate, DueDate, 1, source);
            if (visible)
                dates.Should().ContainSingle().Which.Should().Be(DueDate);
            else
                dates.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    public void GetVisibleScheduledDates_WithLoadedLogs_IgnoresDeletedLogsAndKeepsCompletions(
        bool complete, bool skip, bool deleteLog, bool visible)
    {
        var habit = CreateHabit();
        if (complete)
        {
            var log = habit.Log(DueDate).Value;
            if (deleteLog)
                log.SoftDelete();
        }
        if (skip)
        {
            var log = habit.SkipFlexible(DueDate).Value;
            if (deleteLog)
                log.SoftDelete();
        }

        var dates = HabitScheduleFilters.GetVisibleScheduledDates(
            habit, DueDate, DueDate, 1, null);

        if (visible)
            dates.Should().ContainSingle().Which.Should().Be(DueDate);
        else
            dates.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "none", false)]
    [InlineData(true, "skip", false)]
    [InlineData(true, "deleted-completion", false)]
    [InlineData(true, "completion", true)]
    [InlineData(false, "none", false)]
    [InlineData(false, "deleted-completion", false)]
    [InlineData(false, "completion", true)]
    public void FilterScheduledHabits_WithLoadedChildLogs_OnlyIncludesVisibleHistory(
        bool flexible, string logKind, bool visible)
    {
        var selectedDate = DueDate.AddDays(1);
        var parent = Habit.Create(new HabitCreateParams(
            UserId, "Parent", null, null, DueDate.AddDays(10))).Value;
        var child = Habit.Create(new HabitCreateParams(
            UserId, "Child", FrequencyUnit.Week, 3, selectedDate.AddDays(1),
            ParentHabitId: parent.Id, IsFlexible: flexible)).Value;
        if (logKind == "skip")
            child.SkipFlexible(selectedDate).IsSuccess.Should().BeTrue();
        if (logKind is "completion" or "deleted-completion")
        {
            var log = child.Log(selectedDate).Value;
            if (logKind == "deleted-completion")
                log.SoftDelete();
        }

        var lookup = new[] { parent, child }.ToLookup(habit => habit.ParentHabitId);
        var items = HabitScheduleFilters.FilterScheduledHabits(
            [parent], selectedDate, selectedDate, false, lookup, 1,
            new HashSet<Guid>(), null);

        if (visible)
            items.Should().ContainSingle().Which.habit.Id.Should().Be(parent.Id);
        else
            items.Should().BeEmpty();
    }

    [Fact]
    public void FilterScheduledHabits_WithLoadedLogs_ExcludesFlexibleHabitAfterTargetIsMet()
    {
        var habit = CreateHabit(quantity: 1);
        habit.Log(DueDate).IsSuccess.Should().BeTrue();
        var nextDate = DueDate.AddDays(1);
        var lookup = new[] { habit }.ToLookup(item => item.ParentHabitId);

        var items = HabitScheduleFilters.FilterScheduledHabits(
            [habit], nextDate, nextDate, false, lookup, 1,
            new HashSet<Guid>(), null);

        items.Should().BeEmpty();
    }

    [Fact]
    public void FilterScheduledHabits_WithLoadedLogs_KeepsRemainingFlexibleDateDue()
    {
        var habit = CreateHabit(quantity: 2);
        habit.SkipFlexible(DueDate).IsSuccess.Should().BeTrue();
        var nextDate = DueDate.AddDays(1);
        var lookup = new[] { habit }.ToLookup(item => item.ParentHabitId);

        var items = HabitScheduleFilters.FilterScheduledHabits(
            [habit], nextDate, nextDate, false, lookup, 1,
            new HashSet<Guid>(), null);

        items.Should().ContainSingle().Which.scheduledDates.Should()
            .ContainSingle().Which.Should().Be(nextDate);
    }

    [Fact]
    public void ScheduleMapContext_WithoutDateCache_ReturnsVisibleDatesFromLoadedLogs()
    {
        var habit = CreateHabit();
        habit.SkipFlexible(DueDate).IsSuccess.Should().BeTrue();
        var lookup = new[] { habit }.ToLookup(item => item.ParentHabitId);
        var context = new ScheduleMapContext(lookup, 1,
            DateFrom: DueDate, DateTo: DueDate.AddDays(1));

        context.GetScheduledDates(habit).Should().ContainSingle()
            .Which.Should().Be(DueDate.AddDays(1));
    }
}
