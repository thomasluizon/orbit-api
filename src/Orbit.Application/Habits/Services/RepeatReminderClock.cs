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
        var candidate = firstAtUtc;
        for (var next = Next(candidate, options, zone); next <= nowAtUtc; next = Next(candidate, options, zone))
            candidate = next;
        return candidate;
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

    private static DateTime LocalInstant(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
