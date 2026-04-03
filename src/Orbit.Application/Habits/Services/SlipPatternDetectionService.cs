using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

public static class SlipPatternDetectionService
{
    private const int LookbackDays = 60;
    private const int MinTotalLogs = 3;
    private const int MinBucketCountForTimePeak = 3;

    public static SlipPattern? DetectPattern(
        IReadOnlyList<HabitLog> logs,
        Guid habitId,
        TimeZoneInfo userTimeZone)
    {
        var cutoff = DateTime.UtcNow.AddDays(-LookbackDays);
        var recentLogs = logs.Where(l => l.CreatedAtUtc >= cutoff).ToList();

        if (recentLogs.Count < MinTotalLogs)
            return null;

        // Convert to user local time and extract (DayOfWeek, Hour)
        var localEntries = recentLogs.Select(l =>
        {
            var localTime = TimeZoneInfo.ConvertTimeFromUtc(l.CreatedAtUtc, userTimeZone);
            return (localTime.DayOfWeek, localTime.Hour);
        }).ToList();

        // Group by DayOfWeek, filter to days with 3+ occurrences
        var dayGroups = localEntries
            .GroupBy(e => e.DayOfWeek)
            .Where(g => g.Count() >= MinTotalLogs)
            .ToList();

        if (dayGroups.Count == 0)
            return null;

        SlipPattern? strongest = null;

        foreach (var dayGroup in dayGroups)
        {
            // Bucket hours into 2-hour windows, pick peak window
            var hourBuckets = dayGroup
                .GroupBy(e => e.Hour / 2)
                .OrderByDescending(g => g.Count())
                .ToList();

            var topBucket = hourBuckets[0];

            // Only assign a peak hour if the top bucket has meaningful concentration
            int? peakHour = topBucket.Count() >= MinBucketCountForTimePeak
                ? topBucket.Key * 2 + 1
                : null;

            var occurrenceCount = dayGroup.Count();
            var confidence = (double)occurrenceCount / recentLogs.Count;

            var pattern = new SlipPattern(
                habitId,
                dayGroup.Key,
                peakHour,
                occurrenceCount,
                confidence);

            if (strongest is null || confidence > strongest.Confidence)
                strongest = pattern;
        }

        return strongest;
    }
}
