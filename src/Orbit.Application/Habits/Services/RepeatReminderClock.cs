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
        if (candidate < nowAtUtc.AddDays(-3))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(nowAtUtc, zone);
            candidate = LocalInstant(DateOnly.FromDateTime(local).AddDays(-2).ToDateTime(options.ActiveStart.Value), zone);
        }
        while (Next(candidate, options, zone) <= nowAtUtc)
            candidate = Next(candidate, options, zone);
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
        return LocalInstant(date.ToDateTime(start), zone);
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
