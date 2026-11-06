using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Habits.Services;

public sealed class StaleBadHabitPredicateTests
{
    private static readonly DateOnly Today = new(2026, 6, 10);

    [Fact]
    public void IsStaleBadHabit_AgreesWithTheQueryExpressionForEveryHabitShape()
    {
        var owner = Guid.NewGuid();
        var query = HabitScheduleService.StaleBadHabitsFor(owner, Today).Compile();
        var habits = (
            from schedule in new[] { HabitSchedule.Recurring, HabitSchedule.Flexible, HabitSchedule.OneTime }
            from isBad in new[] { true, false }
            from dueOffset in new[] { -3, 0, 2 }
            from endOffset in new int?[] { null, -1, 0, 5 }
            from userId in new[] { owner, Guid.NewGuid() }
            select Habit.Create(new HabitCreateParams(schedule, userId, "Late snacks",
                schedule == HabitSchedule.OneTime ? null : FrequencyUnit.Day,
                schedule == HabitSchedule.OneTime ? null : 1,
                Today.AddDays(dueOffset), IsBadHabit: isBad,
                EndDate: endOffset is null ? null : Today.AddDays(endOffset.Value))))
            .Where(result => result.IsSuccess).Select(result => result.Value).ToArray();

        habits.Should().HaveCountGreaterThan(20);
        habits.Should().Contain(habit => query(habit)).And.Contain(habit => !query(habit));
        habits.Should().OnlyContain(habit => HabitScheduleService.IsStaleBadHabit(habit, owner, Today) == query(habit));
    }
}
