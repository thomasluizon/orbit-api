using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Benchmarks;

/// <summary>
/// Builds the representative, in-memory inputs the hot-path benchmarks measure against: a daily habit
/// carrying a year of completion logs, a portfolio of such habits, and the pre-materialized date sets
/// the streak walker consumes. No database or I/O — the three targets are pure functions, so the
/// fixtures exist only to give them realistic (roughly a year of dates, twenty habits) work to chew.
/// </summary>
internal static class BenchmarkFixtures
{
    private static readonly Guid OwnerId = Guid.NewGuid();

    internal static Habit DailyHabitWithHistory(DateOnly start, DateOnly today, int completeEveryNthDay)
    {
        var habit = Habit.Create(new HabitCreateParams(
            OwnerId, "Benchmark habit", FrequencyUnit.Day, 1, DueDate: start)).Value;
        BackdateCreation(habit, start);

        for (var date = start; date <= today; date = date.AddDays(1))
        {
            if ((date.DayNumber - start.DayNumber) % completeEveryNthDay == 0)
                habit.Log(date, advanceDueDate: false);
        }

        return habit;
    }

    internal static List<Habit> HabitPortfolio(DateOnly start, DateOnly today, int habitCount)
    {
        var habits = new List<Habit>(habitCount);
        for (var index = 0; index < habitCount; index++)
            habits.Add(DailyHabitWithHistory(start, today, completeEveryNthDay: 1 + (index % 3)));
        return habits;
    }

    internal static (HashSet<DateOnly> Expected, HashSet<DateOnly> Completed, HashSet<DateOnly> Frozen) DateSets(
        DateOnly from, DateOnly to, int completeEveryNthDay, int freezeEveryNthDay)
    {
        var expected = new HashSet<DateOnly>();
        var completed = new HashSet<DateOnly>();
        var frozen = new HashSet<DateOnly>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            expected.Add(date);
            var offset = date.DayNumber - from.DayNumber;
            if (offset % completeEveryNthDay == 0)
                completed.Add(date);
            else if (offset % freezeEveryNthDay == 0)
                frozen.Add(date);
        }

        return (expected, completed, frozen);
    }

    private static void BackdateCreation(Habit habit, DateOnly date) =>
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
