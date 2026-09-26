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
}
