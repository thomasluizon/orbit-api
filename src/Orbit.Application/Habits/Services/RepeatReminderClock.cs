using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Services;

public static class RepeatReminderClock
{
    public static DateTime Next(DateTime anchorAtUtc, RepeatReminderOptions options, TimeZoneInfo zone) =>
        MoveIntoWindow(anchorAtUtc.AddMinutes(options.IntervalMinutes), options, zone);

    public static DateTime LatestDue(DateTime firstAtUtc, DateTime nowAtUtc, RepeatReminderOptions options, TimeZoneInfo zone)
    {
        if (firstAtUtc > nowAtUtc)
            return firstAtUtc;
        if (options.ActiveStart is null)
        {
            var interval = TimeSpan.FromMinutes(options.IntervalMinutes).Ticks;
            return firstAtUtc.AddTicks((nowAtUtc - firstAtUtc).Ticks / interval * interval);
        }
        var guards = OffsetChangeGuards(firstAtUtc, nowAtUtc, zone);
        var guardIndex = 0;
        var phases = new Dictionary<TimeOnly, DateTime>();
        var candidate = firstAtUtc;
        while (true)
        {
            while (guardIndex < guards.Count && guards[guardIndex].End <= candidate)
                guardIndex++;
            if (guardIndex < guards.Count && guards[guardIndex].Start <= candidate)
                phases.Clear();
            else
            {
                var phase = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(candidate, zone));
                if (phases.TryGetValue(phase, out var previous))
                {
                    var cycleTicks = (candidate - previous).Ticks;
                    var limit = guardIndex < guards.Count && guards[guardIndex].Start <= nowAtUtc
                        ? guards[guardIndex].Start.AddTicks(-1) : nowAtUtc;
                    var cycles = (limit - candidate).Ticks / cycleTicks;
                    candidate = candidate.AddTicks(cycles * cycleTicks);
                }
                phases[phase] = candidate;
            }
            var next = Next(candidate, options, zone);
            if (next > nowAtUtc)
                break;
            candidate = next;
        }
        return candidate;
    }

    /// <summary>
    /// A repeated local phase permits skipping whole cycles only while the UTC offset is stable.
    /// Guard whole days around adjustment boundaries so gaps, overlaps and rule changes use Next.
    /// </summary>
    private static List<(DateTime Start, DateTime End)> OffsetChangeGuards(DateTime firstAtUtc, DateTime nowAtUtc, TimeZoneInfo zone)
    {
        var guards = new List<(DateTime Start, DateTime End)>();
        void Guard(DateTime localDate)
        {
            var start = new DateTime(Math.Max(DateTime.MinValue.Ticks, localDate.Ticks - 2 * TimeSpan.TicksPerDay), DateTimeKind.Utc);
            var end = new DateTime(Math.Min(DateTime.MaxValue.Ticks, localDate.Ticks + 3 * TimeSpan.TicksPerDay), DateTimeKind.Utc);
            if (end >= firstAtUtc && start <= nowAtUtc)
                guards.Add((start, end));
        }
        foreach (var rule in zone.GetAdjustmentRules())
        {
            if (rule.DateEnd.Ticks + TimeSpan.TicksPerDay - 1 < firstAtUtc.Ticks - 3 * TimeSpan.TicksPerDay
                || rule.DateStart.Ticks > nowAtUtc.Ticks + 3 * TimeSpan.TicksPerDay)
                continue;
            Guard(rule.DateStart);
            Guard(rule.DateEnd);
            for (var year = Math.Max(rule.DateStart.Year, firstAtUtc.Year - 1);
                year <= Math.Min(rule.DateEnd.Year, nowAtUtc.Year + 1); year++)
            {
                Guard(TransitionDate(year, rule.DaylightTransitionStart));
                Guard(TransitionDate(year, rule.DaylightTransitionEnd));
            }
        }
        return guards.OrderBy(g => g.Start).ToList();
    }

    private static DateTime TransitionDate(int year, TimeZoneInfo.TransitionTime transition)
    {
        var days = DateTime.DaysInMonth(year, transition.Month);
        if (transition.IsFixedDateRule)
            return new DateTime(year, transition.Month, Math.Min(transition.Day, days));
        var first = new DateTime(year, transition.Month, 1);
        var day = 1 + ((int)transition.DayOfWeek - (int)first.DayOfWeek + 7) % 7 + (transition.Week - 1) * 7;
        return new DateTime(year, transition.Month, day > days ? day - 7 : day);
    }

    public static DateTime MoveIntoWindow(DateTime instantAtUtc, RepeatReminderOptions options, TimeZoneInfo zone)
    {
        if (options.ActiveStart is not { } start || options.ActiveEnd is not { } end)
            return instantAtUtc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(instantAtUtc, zone);
        var time = TimeOnly.FromDateTime(local);
        var active = start < end ? time >= start && time < end : time >= start || time < end;
        if (active)
            return instantAtUtc;
        var date = DateOnly.FromDateTime(local);
        if (start < end && time >= end)
            date = date.AddDays(1);
        var localStart = date.ToDateTime(start);
        var candidateAtUtc = LocalInstant(localStart, zone);
        if (candidateAtUtc >= instantAtUtc)
            return candidateAtUtc;
        if (zone.IsAmbiguousTime(localStart))
        {
            var laterAtUtc = DateTime.SpecifyKind(localStart - zone.GetAmbiguousTimeOffsets(localStart).Min(), DateTimeKind.Utc);
            if (laterAtUtc >= instantAtUtc)
                return laterAtUtc;
        }
        return LocalInstant(date.AddDays(1).ToDateTime(start), zone);
    }

    internal static DateTime LocalInstant(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
