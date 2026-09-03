using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

public record HabitInstanceItem(
    DateOnly Date,
    InstanceStatus Status,
    Guid? LogId);

public static class HabitScheduleService
{
    /// <summary>
    /// Determines if a habit is scheduled on a given date based on its
    /// frequency, quantity, active days, and anchor (due) date.
    /// For flexible habits, they appear on every date in range (filtering by log count is done separately).
    /// </summary>
    public static bool IsHabitDueOnDate(Habit habit, DateOnly target, int weekStartDay = 1)
    {
        if (habit.EndDate.HasValue && target > habit.EndDate.Value)
            return false;

        if (habit.IsFlexible)
        {
            if (habit.FrequencyUnit is null) return false;
            return target >= habit.DueDate
                && IsActiveIntervalWeek(habit, target, weekStartDay);
        }

        var anchor = habit.DueDate;
        var unit = habit.FrequencyUnit;
        var qty = habit.FrequencyQuantity ?? 1;

        if (unit is null)
        {
            return target == habit.DueDate;
        }

        if (target < anchor) return false;

        return MatchesFrequency(habit, target, anchor, unit, qty, weekStartDay);
    }

    /// <summary>
    /// Returns all dates within [from, to] where the habit is scheduled.
    /// </summary>
    public static List<DateOnly> GetScheduledDates(
        Habit habit,
        DateOnly from,
        DateOnly to,
        int weekStartDay = 1)
    {
        if (to.DayNumber - from.DayNumber > AppConstants.MaxRangeDays)
            to = from.AddDays(AppConstants.MaxRangeDays);

        var dates = new List<DateOnly>();
        var current = from;
        while (current <= to)
        {
            if (IsHabitDueOnDate(habit, current, weekStartDay))
                dates.Add(current);
            current = current.AddDays(1);
        }
        return dates;
    }

    /// <summary>
    /// Returns the union of all scheduled dates across a set of habits within [from, to].
    /// Excludes habits that do not contribute to the user-wide streak:
    ///   - bad habits (no "must do" expectation)
    ///   - general habits (no schedule)
    ///   - flexible habits (window-based)
    ///   - one-time tasks already completed
    ///   - soft-deleted habits
    /// </summary>
    public static HashSet<DateOnly> GetUnionScheduledDates(
        IEnumerable<Habit> habits, DateOnly from, DateOnly to, int weekStartDay = 1)
    {
        var union = new HashSet<DateOnly>();
        foreach (var habit in habits)
        {
            if (!IsStreakContributingHabit(habit)) continue;

            foreach (var date in GetScheduledDates(habit, from, to, weekStartDay))
                union.Add(date);
        }
        return union;
    }

    /// <summary>
    /// Like <see cref="GetUnionScheduledDates"/> but uses each habit's CreatedAtUtc as
    /// the earliest possible date instead of the current DueDate. This is necessary for
    /// historical streak calculation because DueDate advances forward on each completion,
    /// making past dates invisible to the normal schedule check.
    /// </summary>
    public static HashSet<DateOnly> GetUnionScheduledDatesForStreak(
        IEnumerable<Habit> habits,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo userTimeZone,
        int weekStartDay = 1)
    {
        var union = new HashSet<DateOnly>();
        foreach (var habit in habits)
        {
            if (!IsStreakContributingHabit(habit)) continue;

            var habitStart = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(habit.CreatedAtUtc, userTimeZone));
            var effectiveFrom = from < habitStart ? habitStart : from;

            foreach (var date in GetStreakScheduledDates(habit, effectiveFrom, to, habitStart, weekStartDay))
                union.Add(date);
        }
        return union;
    }

    private static bool IsStreakContributingHabit(Habit habit)
    {
        if (habit.IsDeleted) return false;
        if (habit.IsBadHabit) return false;
        if (habit.IsGeneral) return false;
        if (habit.IsFlexible) return false;
        if (habit.FrequencyUnit is null && habit.IsCompleted) return false;
        return true;
    }

    /// <summary>
    /// The streak value, and most recent contributing date, as of <paramref name="anchor"/> — the single
    /// schedule-aware streak rule shared by the live recalculation engine and the streak-history series.
    /// Walks <paramref name="from"/>..<paramref name="anchor"/> forward over <paramref name="expectedDates"/>:
    /// non-scheduled days are skipped (they never break or extend a streak), a scheduled completion extends
    /// it, a scheduled freeze bridges it without extending, and a scheduled day that is neither resets it.
    /// An unresolved scheduled <paramref name="anchor"/> reports the streak carried into it (the day is not
    /// yet missed) without breaking, mirroring the live engine's "today not done yet" allowance.
    /// </summary>
    public static (int Streak, DateOnly? LastActiveDate) ComputeStreakAsOf(
        HashSet<DateOnly> expectedDates,
        HashSet<DateOnly> completionDates,
        HashSet<DateOnly> freezeDates,
        DateOnly from,
        DateOnly anchor)
    {
        var run = new StreakRun();
        for (var date = from; date <= anchor; date = date.AddDays(1))
            run.Advance(date, expectedDates, completionDates, freezeDates, isAnchor: date == anchor);
        return (run.Streak, run.LastActiveDate);
    }

    /// <summary>
    /// The day-by-day streak series over [<paramref name="from"/>, <paramref name="to"/>], each point being the
    /// streak as if that day were today. Uses the same forward walk as <see cref="ComputeStreakAsOf"/> seeded from
    /// <paramref name="seedFrom"/> (a lookback before <paramref name="from"/>) so the streak entering the window is
    /// correct, then emits only the in-window days. The value on <paramref name="to"/> equals the user's live
    /// current streak for the same inputs.
    /// </summary>
    public static List<(DateOnly Date, int Streak)> BuildStreakSeries(
        HashSet<DateOnly> expectedDates,
        HashSet<DateOnly> completionDates,
        HashSet<DateOnly> freezeDates,
        DateOnly seedFrom,
        DateOnly from,
        DateOnly to)
    {
        var points = new List<(DateOnly Date, int Streak)>();
        var run = new StreakRun();
        for (var date = seedFrom; date <= to; date = date.AddDays(1))
        {
            var streak = run.Advance(date, expectedDates, completionDates, freezeDates, isAnchor: date == to);
            if (date >= from)
                points.Add((date, streak));
        }
        return points;
    }

    private sealed class StreakRun
    {
        public int Streak { get; private set; }
        public DateOnly? LastActiveDate { get; private set; }

        public int Advance(
            DateOnly date,
            HashSet<DateOnly> expectedDates,
            HashSet<DateOnly> completionDates,
            HashSet<DateOnly> freezeDates,
            bool isAnchor)
        {
            if (!expectedDates.Contains(date))
                return Streak;

            if (completionDates.Contains(date))
            {
                Streak++;
                LastActiveDate = date;
                return Streak;
            }

            if (freezeDates.Contains(date))
            {
                LastActiveDate = date;
                return Streak;
            }

            var carried = Streak;
            if (!isAnchor)
            {
                Streak = 0;
                LastActiveDate = null;
            }
            return carried;
        }
    }

    /// <summary>
    /// Returns occurrences used only for the first computation of a closed-month recap. This is
    /// separate from <see cref="GetScheduledDates"/> and the live streak path because it aligns
    /// recurrence to <see cref="Habit.ScheduledStartDate"/> and bounds it by lifecycle dates. A
    /// later cadence edit cannot reconstruct the earlier cadence because the model stores no cadence
    /// history, so the computed recap is persisted and this helper never recomputes a stored month.
    /// </summary>
    public static List<DateOnly> GetHistoricalScheduledDates(
        Habit habit,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo userTimeZone,
        int weekStartDay = 1)
    {
        var createdDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(habit.CreatedAtUtc, DateTimeKind.Utc),
            userTimeZone));
        var recurrenceAnchor = habit.ScheduledStartDate ?? habit.DueDate;
        var scheduledStart = habit.ScheduledStartDate ?? createdDate;
        var effectiveFrom = Max(from, createdDate, scheduledStart);
        var effectiveTo = habit.EndDate.HasValue && habit.EndDate.Value < to
            ? habit.EndDate.Value
            : to;

        if (habit.DeletedAtUtc.HasValue)
        {
            var deletedDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(habit.DeletedAtUtc.Value, DateTimeKind.Utc),
                userTimeZone));
            if (deletedDate < effectiveTo)
                effectiveTo = deletedDate;
        }

        if (effectiveTo < effectiveFrom)
            return [];
        if (effectiveTo.DayNumber - effectiveFrom.DayNumber > AppConstants.MaxRangeDays)
            effectiveTo = effectiveFrom.AddDays(AppConstants.MaxRangeDays);

        var dates = new List<DateOnly>();
        for (var current = effectiveFrom; current <= effectiveTo; current = current.AddDays(1))
        {
            if (IsHistoricallyDueOnDate(habit, current, recurrenceAnchor, weekStartDay))
                dates.Add(current);
        }
        return dates;
    }

    private static bool IsHistoricallyDueOnDate(
        Habit habit,
        DateOnly target,
        DateOnly recurrenceAnchor,
        int weekStartDay)
    {
        if (habit.IsFlexible)
            return habit.FrequencyUnit is not null
                && IsActiveIntervalWeek(habit, target, weekStartDay, recurrenceAnchor);

        var unit = habit.FrequencyUnit;
        if (unit is null)
            return target == habit.DueDate;

        return MatchesFrequency(
            habit,
            target,
            recurrenceAnchor,
            unit,
            habit.FrequencyQuantity ?? 1,
            weekStartDay,
            recurrenceAnchor);
    }

    /// <summary>
    /// Applies the habit's current cadence using its moving due date as the alignment anchor while
    /// allowing the live streak lookback to inspect dates back to account activity.
    /// </summary>
    public static bool IsHabitDueOnDateForStreakLookback(
        Habit habit,
        DateOnly target,
        DateOnly habitCreationDate,
        int weekStartDay = 1)
    {
        if (habit.EndDate.HasValue && target > habit.EndDate.Value)
            return false;

        if (habit.IsFlexible)
        {
            if (habit.FrequencyUnit is null) return false;
            return target >= habitCreationDate
                && IsActiveIntervalWeek(habit, target, weekStartDay);
        }

        var unit = habit.FrequencyUnit;
        if (unit is null)
            return target == habit.DueDate;

        if (target < habitCreationDate) return false;

        return MatchesFrequency(habit, target, habit.DueDate, unit, habit.FrequencyQuantity ?? 1, weekStartDay);
    }

    private static List<DateOnly> GetStreakScheduledDates(
        Habit habit,
        DateOnly from,
        DateOnly to,
        DateOnly habitCreationDate,
        int weekStartDay)
    {
        if (to.DayNumber - from.DayNumber > AppConstants.MaxRangeDays)
            to = from.AddDays(AppConstants.MaxRangeDays);

        var dates = new List<DateOnly>();
        for (var current = from; current <= to; current = current.AddDays(1))
        {
            if (IsHabitDueOnDateForStreakLookback(habit, current, habitCreationDate, weekStartDay))
                dates.Add(current);
        }
        return dates;
    }

    private static DateOnly Max(DateOnly first, DateOnly second, DateOnly third) =>
        first > second
            ? first > third ? first : third
            : second > third ? second : third;

    /// <summary>
    /// True when a recurring, non-flexible, non-bad habit has an unresolved past
    /// occurrence, meaning its <see cref="Habit.DueDate"/> has fallen before today and the current
    /// cadence schedules an occurrence without a completion or skip log within the bounded
    /// schedule horizon ending before today. This is the single overdue signal shared by the
    /// schedule query and the log/skip commands.
    /// </summary>
    public static bool HasMissedPastOccurrence(Habit habit, DateOnly today, int weekStartDay)
    {
        if (habit.FrequencyUnit is null || habit.IsBadHabit || habit.IsFlexible)
            return false;
        if (habit.DueDate >= today)
            return false;
        if ((!habit.EndDate.HasValue || habit.DueDate <= habit.EndDate.Value)
            && IsHabitDueOnDate(habit, habit.DueDate, weekStartDay)
            && !IsOccurrenceResolved(habit, habit.DueDate))
            return true;

        var latestOccurrence = today.AddDays(-1);
        if (habit.EndDate.HasValue && habit.EndDate.Value < latestOccurrence)
            latestOccurrence = habit.EndDate.Value;

        var earliestOccurrence = habit.DueDate;
        var boundedStart = latestOccurrence.AddDays(-Math.Min(
            AppConstants.MaxRangeDays - 1,
            latestOccurrence.DayNumber));
        if (earliestOccurrence < boundedStart)
            earliestOccurrence = boundedStart;

        for (var date = earliestOccurrence; date <= latestOccurrence; date = date.AddDays(1))
        {
            if (IsHabitDueOnDate(habit, date, weekStartDay)
                && !IsOccurrenceResolved(habit, date))
                return true;
        }

        return false;
    }

    private static bool IsOccurrenceResolved(Habit habit, DateOnly date) =>
        habit.Logs.Any(log =>
            !log.IsDeleted
            && log.Date == date
            && (log.Value == 0 || log.Value > 0));

    /// <summary>
    /// True when the habit has an active completion log (Value &gt; 0) on any date within
    /// [<paramref name="dateFrom"/>, <paramref name="dateTo"/>]. Skip logs (Value == 0) do not
    /// count. This is the date-scoped "done in range" signal shared by the schedule query and the
    /// daily summary — deliberately distinct from <see cref="Habit.IsCompleted"/>, which is a
    /// sticky lifetime flag (a one-time task stays completed forever) and must never be used to
    /// decide whether a habit was done on a particular day.
    /// </summary>
    public static bool HasCompletedLogInRange(Habit habit, DateOnly dateFrom, DateOnly dateTo) =>
        HasCompletedLogInRange(habit.Logs, dateFrom, dateTo);

    internal static bool HasCompletedLogInRange(
        IEnumerable<HabitLog> logs,
        DateOnly dateFrom,
        DateOnly dateTo) =>
        logs.Any(l => !l.IsDeleted && l.Date >= dateFrom && l.Date <= dateTo && l.Value > 0);

    /// <summary>
    /// True when the habit has an unresolved occurrence strictly before
    /// <paramref name="referenceDate"/>: a one-time task past its (still-uncompleted) due date,
    /// or a recurring habit whose DueDate has fallen behind and that is not due on the reference
    /// date. Flexible and bad habits are never overdue. This is the single overdue rule shared by
    /// the schedule query (<c>GetHabitScheduleQuery</c>) and the daily summary (<c>AiSummaryService</c>).
    /// </summary>
    public static bool IsOverdueOnDate(Habit habit, DateOnly referenceDate, int weekStartDay = 1)
    {
        if (habit.IsFlexible || habit.IsBadHabit)
            return false;

        if (habit.FrequencyUnit is null)
            return !habit.IsCompleted
                && habit.DueDate < referenceDate
                && (!habit.EndDate.HasValue || habit.EndDate.Value >= referenceDate);

        if (IsHabitDueOnDate(habit, referenceDate, weekStartDay))
            return false;

        return HasMissedPastOccurrence(habit, referenceDate, weekStartDay);
    }

    /// <summary>
    /// Checks if the flexible habit still needs completions within the window containing target.
    /// Weekly windows anchor on the owner's <paramref name="weekStartDay"/> (0 = Sunday, 1 = Monday).
    /// </summary>
    public static bool IsFlexibleHabitDueOnDate(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs, int weekStartDay)
    {
        if (!habit.IsFlexible || habit.FrequencyUnit is null) return false;
        if (target < habit.DueDate) return false;
        if (!IsActiveIntervalWeek(habit, target, weekStartDay)) return false;
        return GetRemainingCompletions(habit, target, logs, weekStartDay) > 0;
    }

    /// <summary>
    /// Returns the start of the window containing the target date. Weekly windows anchor on the
    /// owner's <paramref name="weekStartDay"/> (0 = Sunday, 1 = Monday) so the server window matches
    /// the client's WeekStartDay-driven calendars.
    /// </summary>
    public static DateOnly GetWindowStart(Habit habit, DateOnly target, int weekStartDay)
    {
        return habit.FrequencyUnit switch
        {
            FrequencyUnit.Day => target,
            FrequencyUnit.Week => WeekMath.WeekStart(target, weekStartDay),
            FrequencyUnit.Month => new DateOnly(target.Year, target.Month, 1),
            FrequencyUnit.Year => new DateOnly(target.Year, 1, 1),
            _ => target
        };
    }

    /// <summary>
    /// Returns the end of the window containing the target date. Weekly windows anchor on the
    /// owner's <paramref name="weekStartDay"/> (0 = Sunday, 1 = Monday).
    /// </summary>
    public static DateOnly GetWindowEnd(Habit habit, DateOnly target, int weekStartDay)
    {
        return habit.FrequencyUnit switch
        {
            FrequencyUnit.Day => target,
            FrequencyUnit.Week => GetWindowStart(habit, target, weekStartDay).AddDays(6),
            FrequencyUnit.Month => new DateOnly(target.Year, target.Month, DateTime.DaysInMonth(target.Year, target.Month)),
            FrequencyUnit.Year => new DateOnly(target.Year, 12, 31),
            _ => target
        };
    }

    /// <summary>
    /// Count of completion logs (Value > 0) within the window containing target.
    /// Skip logs (Value == 0) are excluded.
    /// </summary>
    public static int GetCompletedInWindow(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs, int weekStartDay)
    {
        if (!IsActiveIntervalWeek(habit, target, weekStartDay))
            return 0;

        var start = GetWindowStart(habit, target, weekStartDay);
        var end = GetWindowEnd(habit, target, weekStartDay);
        return logs.Count(l => l.Date >= start && l.Date <= end && l.Value > 0);
    }

    /// <summary>
    /// Count of skip logs (Value == 0) within the window containing target.
    /// </summary>
    public static int GetSkippedInWindow(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs, int weekStartDay)
    {
        if (!IsActiveIntervalWeek(habit, target, weekStartDay))
            return 0;

        var start = GetWindowStart(habit, target, weekStartDay);
        var end = GetWindowEnd(habit, target, weekStartDay);
        return logs.Count(l => l.Date >= start && l.Date <= end && l.Value == 0);
    }

    /// <summary>
    /// How many more completions are needed in the window containing target.
    /// Skips reduce the target count for the period.
    /// </summary>
    public static int GetRemainingCompletions(Habit habit, DateOnly target, IReadOnlyCollection<HabitLog> logs, int weekStartDay)
    {
        var targetCount = habit.FrequencyQuantity ?? 1;
        var skipped = GetSkippedInWindow(habit, target, logs, weekStartDay);
        var adjustedTarget = Math.Max(0, targetCount - skipped);
        var completed = GetCompletedInWindow(habit, target, logs, weekStartDay);
        return Math.Max(0, adjustedTarget - completed);
    }

    /// <summary>
    /// Computes per-date instances for a habit within a date range, including an overdue lookback window.
    /// Each instance has its own status (Pending, Completed, Overdue) based on logs and the user's today.
    /// The forward horizon is capped at <see cref="AppConstants.MaxInstanceHorizonDays"/> days from
    /// <paramref name="dateFrom"/> so the array stays bounded when a caller requests the full allowed
    /// range (the schedule query permits up to <see cref="AppConstants.MaxRangeDays"/> days); the cap
    /// exceeds every real caller's window (calendar-month ≤ 62 days, schedule interval ≤ 14 days).
    /// </summary>
    public static List<HabitInstanceItem> GetInstances(
        Habit habit,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday,
        int overdueWindowDays = AppConstants.DefaultOverdueWindowDays,
        int weekStartDay = 1)
    {
        if (habit.IsCompleted || habit.IsFlexible)
            return [];

        if (dateTo.DayNumber - dateFrom.DayNumber > AppConstants.MaxInstanceHorizonDays)
            dateTo = dateFrom.AddDays(AppConstants.MaxInstanceHorizonDays);

        if (habit.FrequencyUnit is null)
            return GetOneTimeTaskInstance(habit, dateFrom, dateTo, userToday);

        return GetRecurringInstances(habit, dateFrom, dateTo, userToday, overdueWindowDays, weekStartDay);
    }

    private static List<HabitInstanceItem> GetOneTimeTaskInstance(
        Habit habit, DateOnly dateFrom, DateOnly dateTo, DateOnly userToday)
    {
        if (habit.DueDate < dateFrom || habit.DueDate > dateTo)
            return [];

        var log = habit.Logs.FirstOrDefault(l => l.Date == habit.DueDate);
        var status = ResolveInstanceStatus(log, habit.DueDate, userToday, habit.IsBadHabit);
        return [new HabitInstanceItem(habit.DueDate, status, log?.Id)];
    }

    private static List<HabitInstanceItem> GetRecurringInstances(
        Habit habit,
        DateOnly dateFrom,
        DateOnly dateTo,
        DateOnly userToday,
        int overdueWindowDays,
        int weekStartDay)
    {
        var lookbackStart = dateFrom.AddDays(-overdueWindowDays);
        if (lookbackStart < habit.DueDate)
            lookbackStart = habit.DueDate;

        var scheduledDates = GetScheduledDates(habit, lookbackStart, dateTo, weekStartDay);
        var completedLogDatesInRange = habit.Logs
            .Where(l => l.Value > 0 && l.Date >= dateFrom && l.Date <= dateTo)
            .Select(l => l.Date);
        var instanceDates = scheduledDates
            .Concat(completedLogDatesInRange)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
        var logsByDate = habit.Logs
            .GroupBy(l => l.Date)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(l => l.Value > 0).ThenByDescending(l => l.CreatedAtUtc).First());
        var instances = new List<HabitInstanceItem>(instanceDates.Count);

        foreach (var date in instanceDates)
        {
            logsByDate.TryGetValue(date, out var log);
            var status = ResolveInstanceStatus(log, date, userToday, habit.IsBadHabit);
            instances.Add(new HabitInstanceItem(date, status, log?.Id));
        }

        return instances;
    }

    private static InstanceStatus ResolveInstanceStatus(
        HabitLog? log, DateOnly date, DateOnly userToday, bool isBadHabit)
    {
        if (log is not null) return InstanceStatus.Completed;
        if (isBadHabit) return InstanceStatus.Pending;
        if (date < userToday) return InstanceStatus.Overdue;
        return InstanceStatus.Pending;
    }

    /// <summary>
    /// Returns an appropriate lookback window (in days) for overdue detection
    /// based on the habit's frequency unit and quantity.
    /// </summary>
    public static int GetLookbackDays(FrequencyUnit? unit, int qty)
    {
        return unit switch
        {
            FrequencyUnit.Day => qty,
            FrequencyUnit.Week => qty * 7,
            FrequencyUnit.Month => qty * 31,
            FrequencyUnit.Year => Math.Min(qty * 366, 366),
            _ => 7
        };
    }

    /// <summary>
    /// Advances stale bad habit DueDates so they appear on the correct next scheduled day.
    /// </summary>
    public static async Task AdvanceStaleBadHabitDueDates(
        IGenericRepository<Habit> habitRepository, IUnitOfWork unitOfWork,
        Guid userId, DateOnly today, CancellationToken ct, int weekStartDay = 1)
    {
        var staleBadHabits = await habitRepository.FindTrackedAsync(
            h => h.UserId == userId && h.IsBadHabit && h.FrequencyUnit != null && h.FrequencyQuantity != null && h.DueDate < today
                && (!h.EndDate.HasValue || h.EndDate.Value >= today), ct);
        if (staleBadHabits.Count > 0)
        {
            foreach (var habit in staleBadHabits)
                habit.CatchUpDueDate(today, weekStartDay);
            await unitOfWork.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The schedule tail shared by <see cref="IsHabitDueOnDate"/> and the recurrence readers:
    /// applies the active-days filter, then resolves the
    /// frequency-unit modulo against <paramref name="anchor"/>. Both callers reach this only after
    /// establishing a non-null <paramref name="unit"/> and their own earliest-date gate.
    /// </summary>
    private static bool MatchesFrequency(
        Habit habit,
        DateOnly target,
        DateOnly anchor,
        FrequencyUnit? unit,
        int qty,
        int weekStartDay,
        DateOnly? intervalAnchor = null)
    {
        if (habit.Days.Count > 0 && !habit.Days.Contains(target.DayOfWeek))
            return false;

        if (!IsActiveIntervalWeek(habit, target, weekStartDay, intervalAnchor))
            return false;

        return unit switch
        {
            FrequencyUnit.Day => qty == 1 || TrueMod(target.DayNumber - anchor.DayNumber, qty) == 0,
            FrequencyUnit.Week => target.DayOfWeek == anchor.DayOfWeek && TrueMod(WeekDiff(target, anchor), qty) == 0,
            FrequencyUnit.Month => IsMonthlyMatch(target, anchor, qty),
            FrequencyUnit.Year => IsYearlyMatch(target, anchor, qty),
            _ => false
        };
    }

    public static bool IsActiveIntervalWeek(
        Habit habit,
        DateOnly target,
        int weekStartDay,
        DateOnly? recurrenceAnchor = null)
    {
        var intervalWeeks = habit.IntervalWeeks ?? 1;
        if (intervalWeeks == 1)
            return true;

        var anchor = recurrenceAnchor ?? habit.ScheduledStartDate ?? habit.DueDate;
        var targetWeek = WeekMath.WeekStart(target, weekStartDay);
        var anchorWeek = WeekMath.WeekStart(anchor, weekStartDay);
        return TrueMod(WeekDiff(targetWeek, anchorWeek), intervalWeeks) == 0;
    }

    /// <summary>
    /// Monthly match: target day must equal anchor day (clamped to last day of month),
    /// and the month interval must align. This prevents anchor drift after short months
    /// (e.g., Jan 31 anchor still fires on Mar 31, not Mar 28).
    /// </summary>
    private static bool IsMonthlyMatch(DateOnly target, DateOnly anchor, int qty)
    {
        var expectedDay = Math.Min(anchor.Day, DateTime.DaysInMonth(target.Year, target.Month));
        return target.Day == expectedDay && TrueMod(MonthDiff(target, anchor), qty) == 0;
    }

    /// <summary>
    /// Yearly match: same month and day with interval alignment.
    /// For Feb 29 anchors in non-leap years, Feb 28 is used as the fallback firing date.
    /// </summary>
    private static bool IsYearlyMatch(DateOnly target, DateOnly anchor, int qty)
    {
        if (TrueMod(target.Year - anchor.Year, qty) != 0)
            return false;

        if (target.Month == anchor.Month && target.Day == anchor.Day)
            return true;

        if (anchor.Month == 2 && anchor.Day == 29
            && target.Month == 2 && target.Day == 28
            && !DateTime.IsLeapYear(target.Year))
        {
            return true;
        }

        return false;
    }

    private static int TrueMod(int n, int m)
    {
        return ((n % m) + m) % m;
    }

    private static int WeekDiff(DateOnly a, DateOnly b)
    {
        var dayDiff = a.DayNumber - b.DayNumber;
        return dayDiff / 7;
    }

    private static int MonthDiff(DateOnly a, DateOnly b)
    {
        return (a.Year - b.Year) * 12 + (a.Month - b.Month);
    }
}
