using BenchmarkDotNet.Attributes;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;

namespace Orbit.Benchmarks;

/// <summary>
/// Hot-path streak/schedule math run on every Today, calendar, and streak-history request:
/// <see cref="HabitScheduleService.GetScheduledDates"/>, <see cref="HabitScheduleService.ComputeStreakAsOf"/>,
/// and <see cref="HabitScheduleService.BuildStreakSeries"/>, each over roughly a year of dates.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[JsonExporterAttribute.Full]
public class HabitScheduleBenchmarks
{
    private Habit _dailyHabit = null!;
    private HashSet<DateOnly> _expected = null!;
    private HashSet<DateOnly> _completed = null!;
    private HashSet<DateOnly> _frozen = null!;
    private DateOnly _from;
    private DateOnly _to;
    private DateOnly _seriesFrom;
    private DateOnly _seedFrom;

    [GlobalSetup]
    public void Setup()
    {
        _to = new DateOnly(2026, 7, 1);
        _from = _to.AddDays(-365);
        _seriesFrom = _to.AddDays(-90);
        _seedFrom = _from.AddDays(-30);
        _dailyHabit = BenchmarkFixtures.DailyHabitWithHistory(_from, _to, completeEveryNthDay: 2);
        (_expected, _completed, _frozen) = BenchmarkFixtures.DateSets(_seedFrom, _to, completeEveryNthDay: 2, freezeEveryNthDay: 7);
    }

    [Benchmark]
    public List<DateOnly> GetScheduledDates_OneYearDaily() =>
        HabitScheduleService.GetScheduledDates(_dailyHabit, _from, _to);

    [Benchmark]
    public (int Streak, DateOnly? LastActiveDate) ComputeStreakAsOf_OneYear() =>
        HabitScheduleService.ComputeStreakAsOf(_expected, _completed, _frozen, _from, _to);

    [Benchmark]
    public List<(DateOnly Date, int Streak)> BuildStreakSeries_NinetyDayWindow() =>
        HabitScheduleService.BuildStreakSeries(_expected, _completed, _frozen, _seedFrom, _seriesFrom, _to);
}
