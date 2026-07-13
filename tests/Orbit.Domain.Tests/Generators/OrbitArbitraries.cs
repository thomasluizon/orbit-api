using FsCheck;
using FsCheck.Fluent;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Tests.Generators;

/// <summary>
/// A valid-schedule recurring habit (non-flexible, non-bad, with a <see cref="FrequencyUnit"/>).
/// Wrapped so FsCheck can register a distinct <see cref="Arbitrary{T}"/> per habit shape.
/// </summary>
public sealed record RecurringHabit(Habit Habit);

/// <summary>A flexible (window-based) habit with a non-null <see cref="FrequencyUnit"/>.</summary>
public sealed record FlexibleHabit(Habit Habit);

/// <summary>A recurring bad habit (never overdue, never streak-contributing).</summary>
public sealed record BadHabit(Habit Habit);

/// <summary>A level in the ladder's meaningful range (1..2000), kept below the int-overflow ceiling of 100·level².</summary>
public sealed record LadderLevel(int Value);

/// <summary>A total-XP value in a realistic range (0..10,000,000) that spans the anchor table and the quadratic curve without overflowing.</summary>
public sealed record LadderXp(int Value);

/// <summary>A bounded run length / window span (1..366) so day-by-day walks stay fast.</summary>
public sealed record RunLength(int Value);

/// <summary>
/// FsCheck generator hub shared by the Domain and Application property suites. Every arbitrary is
/// bounded so the schedule day-walks (<c>ComputeStreakAsOf</c>/<c>BuildStreakSeries</c>/<c>GetScheduledDates</c>)
/// never loop pathologically, and every generated <see cref="Habit"/> is a <c>Habit.Create</c>-valid
/// combination across all four frequency units plus one-time, flexible, and monthly/yearly anchors.
/// </summary>
public static class OrbitArbitraries
{
    private static readonly Guid HabitOwnerId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly int MinDayNumber = new DateOnly(2000, 1, 1).DayNumber;
    private static readonly int MaxDayNumber = new DateOnly(2035, 12, 31).DayNumber;

    private static readonly int[] LeapYears =
        [2000, 2004, 2008, 2012, 2016, 2020, 2024, 2028, 2032];

    private static Gen<DateOnly> DateGen =>
        Gen.Choose(MinDayNumber, MaxDayNumber).Select(DateOnly.FromDayNumber);

    private static Gen<TimeOnly> TimeGen =>
        Gen.Choose(0, 1439).Select(minutes => new TimeOnly(minutes / 60, minutes % 60));

    /// <summary>
    /// JSON-safe text pool (no lone UTF-16 surrogates, which System.Text.Json rewrites to U+FFFD and
    /// would break round-trip equality) that still covers empty, whitespace-only, leading/trailing
    /// whitespace, exact duplicates, case variants, and multi-byte unicode for trim/blank/distinct paths.
    /// </summary>
    private static Gen<string> SafeTextGen =>
        Gen.Elements(
            "", " ", "\t", "   ",
            "cal-1", " cal-1 ", "cal-1", "CAL-1",
            "primary", "primary ",
            "work@group.calendar.google.com",
            "Café", "café", "日本語カレンダー");

    private static Gen<DateOnly> MonthEndAnchorGen =>
        from year in Gen.Choose(2000, 2035)
        from month in Gen.Choose(1, 12)
        from day in Gen.Elements(28, 29, 30, 31)
        select new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    private static Gen<DateOnly> LeapDayAnchorGen =>
        Gen.Elements(LeapYears).Select(year => new DateOnly(year, 2, 29));

    private static Gen<DateOnly> AnchorDateGen =>
        Gen.OneOf(DateGen, MonthEndAnchorGen, LeapDayAnchorGen);

    private static Habit Build(
        FrequencyUnit? unit, int? quantity, DateOnly dueDate,
        IReadOnlyList<DayOfWeek>? days, bool isFlexible, bool isBadHabit) =>
        Habit.Create(new HabitCreateParams(
            HabitOwnerId, "Property Habit", unit, quantity,
            Days: days, IsBadHabit: isBadHabit, DueDate: dueDate, IsFlexible: isFlexible)).Value;

    private static List<DayOfWeek> DaysFromMask(int mask) =>
        Enumerable.Range(0, 7).Where(bit => (mask & (1 << bit)) != 0).Select(bit => (DayOfWeek)bit).ToList();

    private static Gen<Habit> OneTimeGen =>
        DateGen.Select(due => Build(null, null, due, null, false, false));

    private static Gen<Habit> DailyGen =>
        from due in DateGen
        from qty in Gen.Choose(1, 7)
        select Build(FrequencyUnit.Day, qty, due, null, false, false);

    private static Gen<Habit> DailyWithDaysGen =>
        from due in DateGen
        from mask in Gen.Choose(1, 127)
        select Build(FrequencyUnit.Day, 1, due, DaysFromMask(mask), false, false);

    private static Gen<Habit> WeeklyGen =>
        from due in DateGen
        from qty in Gen.Choose(1, 8)
        select Build(FrequencyUnit.Week, qty, due, null, false, false);

    private static Gen<Habit> MonthlyGen =>
        from due in AnchorDateGen
        from qty in Gen.Choose(1, 6)
        select Build(FrequencyUnit.Month, qty, due, null, false, false);

    private static Gen<Habit> YearlyGen =>
        from due in AnchorDateGen
        from qty in Gen.Choose(1, 3)
        select Build(FrequencyUnit.Year, qty, due, null, false, false);

    private static Gen<Habit> FlexibleGen =>
        from due in DateGen
        from unit in Gen.Elements(FrequencyUnit.Day, FrequencyUnit.Week, FrequencyUnit.Month, FrequencyUnit.Year)
        from qty in Gen.Choose(1, 7)
        select Build(unit, qty, due, null, true, false);

    private static Gen<Habit> BadGen =>
        from due in AnchorDateGen
        from unit in Gen.Elements(FrequencyUnit.Day, FrequencyUnit.Week, FrequencyUnit.Month, FrequencyUnit.Year)
        from qty in Gen.Choose(1, 6)
        select Build(unit, qty, due, null, false, true);

    private static Gen<Habit> RecurringGen =>
        Gen.OneOf(DailyGen, DailyWithDaysGen, WeeklyGen, MonthlyGen, YearlyGen);

    private static Gen<Habit> AnyValidHabitGen =>
        Gen.OneOf(OneTimeGen, DailyGen, DailyWithDaysGen, WeeklyGen, MonthlyGen, YearlyGen, FlexibleGen);

    private static Gen<ChecklistItem> ChecklistItemGen =>
        from text in SafeTextGen
        from isChecked in Gen.Elements(false, true)
        select new ChecklistItem(text, isChecked);

    private static Gen<ScheduledReminderTime> ScheduledReminderTimeGen =>
        from when in Gen.Elements(ScheduledReminderWhen.SameDay, ScheduledReminderWhen.DayBefore)
        from time in TimeGen
        select new ScheduledReminderTime(when, time);

    public static Arbitrary<DateOnly> DateOnlyArb() => DateGen.ToArbitrary();

    public static Arbitrary<TimeOnly> TimeOnlyArb() => TimeGen.ToArbitrary();

    public static Arbitrary<string> StringArb() => SafeTextGen.ToArbitrary();

    public static Arbitrary<Habit> HabitArb() => AnyValidHabitGen.ToArbitrary();

    public static Arbitrary<RecurringHabit> RecurringHabitArb() =>
        RecurringGen.Select(habit => new RecurringHabit(habit)).ToArbitrary();

    public static Arbitrary<FlexibleHabit> FlexibleHabitArb() =>
        FlexibleGen.Select(habit => new FlexibleHabit(habit)).ToArbitrary();

    public static Arbitrary<BadHabit> BadHabitArb() =>
        BadGen.Select(habit => new BadHabit(habit)).ToArbitrary();

    public static Arbitrary<ChecklistItem> ChecklistItemArb() => ChecklistItemGen.ToArbitrary();

    public static Arbitrary<ScheduledReminderTime> ScheduledReminderTimeArb() =>
        ScheduledReminderTimeGen.ToArbitrary();

    public static Arbitrary<LadderLevel> LadderLevelArb() =>
        Gen.Choose(1, 2000).Select(value => new LadderLevel(value)).ToArbitrary();

    public static Arbitrary<LadderXp> LadderXpArb() =>
        Gen.Choose(0, 10_000_000).Select(value => new LadderXp(value)).ToArbitrary();

    public static Arbitrary<RunLength> RunLengthArb() =>
        Gen.Choose(1, 366).Select(value => new RunLength(value)).ToArbitrary();
}
