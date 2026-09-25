using System.Globalization;
using System.Text.Json.Serialization;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Chat;

public record DaySummaryCard(
    DateOnly Date, int Due, int Done, int? CompletionRate,
    int OverdueCount, int CurrentStreak, string SurfaceId = "today");

public record StreakAchievement(string Id, string IconKey, DateTime EarnedAt);

public record StreakCard(
    int CurrentStreak, int LongestStreak, int Level, int TotalXp, int XpForNextLevel,
    DateOnly? LastActiveDate, bool IsFrozenToday, IReadOnlyList<DateOnly> RecentFreezeDates,
    IReadOnlyList<StreakAchievement> RecentAchievements,
    string SurfaceId = "progress");

public record CalendarCardEvent(string Title, string Start, string? End, bool IsAllDay);

public record CalendarCardSync(bool Enabled, string Status, DateTime? LastSyncedAt);

public record CalendarCard(
    IReadOnlyList<CalendarCardEvent> Events,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarCardSync? Sync,
    string SurfaceId = "calendar");

public static class StatusCardBuilder
{
    public const string DayDirective = "[[orbit:day]]";
    public const string StreakDirective = "[[orbit:streak]]";
    public const string CalendarDirective = "[[orbit:calendar]]";
    public const string DayPrompt = "For today's habit summary, write one short line without figures, then emit [[orbit:day]] last.";
    public const string StreakPrompt = "After get_gamification_overview succeeds, write one short line without figures, then emit [[orbit:streak]] last.";
    public const string CalendarPrompt = "After get_calendar_overview succeeds, write one short line without listing events, then emit [[orbit:calendar]] last.";

    public static DaySummaryCard BuildDay(DateOnly date, RetrospectiveMetrics metrics, int overdueCount) =>
        new(date, metrics.TotalScheduled,
            metrics.CompletionSeries?.Points.SingleOrDefault()?.Completed ?? 0,
            metrics.TotalScheduled == 0 ? null : metrics.CompletionRate,
            overdueCount, metrics.CurrentStreak);

    public static StreakCard? BuildStreak(GamificationOverviewPayload? payload)
    {
        if (payload?.Profile is not { } profile || payload.Streak is not { } streak)
            return null;

        var achievements = payload.Achievements?.Achievements ?? profile.Achievements;
        return new StreakCard(
            streak.CurrentStreak, streak.LongestStreak, profile.Level, profile.TotalXp,
            profile.XpForNextLevel, streak.LastActiveDate, streak.IsFrozenToday,
            streak.RecentFreezeDates,
            achievements.Where(item => item.EarnedAtUtc.HasValue)
                .OrderByDescending(item => item.EarnedAtUtc)
                .Take(6)
                .Select(item => new StreakAchievement(item.Id, item.IconKey, item.EarnedAtUtc!.Value))
                .ToList());
    }

    public static CalendarCard? BuildCalendar(CalendarOverviewPayload? payload)
    {
        if (payload is null)
            return null;

        var events = payload.Events
            .OrderBy(item => item.StartUtc)
            .ThenBy(item => item.StartDate, StringComparer.Ordinal)
            .Take(10)
            .Select(item => new CalendarCardEvent(
                item.Title,
                item.StartUtc?.ToString("O", CultureInfo.InvariantCulture) ?? item.StartDate ?? string.Empty,
                item.EndUtc?.ToString("O", CultureInfo.InvariantCulture) ?? item.EndTime,
                item.StartTime is null))
            .ToList();
        var sync = payload.AutoSyncState is { } state
            ? new CalendarCardSync(state.Enabled, state.Status.ToString(), state.LastSyncedAt)
            : null;
        return new CalendarCard(events, sync);
    }
}
