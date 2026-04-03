using System.Reflection;
using FluentAssertions;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Services;

public class SlipPatternDetectionServiceTests
{
    private static readonly Guid HabitId = Guid.NewGuid();
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static HabitLog CreateLog(DateTime createdAtUtc)
    {
        // HabitLog.Create is internal, InternalsVisibleTo is already set for Orbit.Application.Tests
        var date = DateOnly.FromDateTime(createdAtUtc);
        var log = HabitLog.Create(HabitId, date, 1);

        // Override CreatedAtUtc via reflection since the factory always sets DateTime.UtcNow
        var prop = typeof(HabitLog).GetProperty("CreatedAtUtc", BindingFlags.Public | BindingFlags.Instance);
        prop!.GetSetMethod(true)!.Invoke(log, [createdAtUtc]);

        return log;
    }

    [Fact]
    public void DetectPattern_InsufficientData_ReturnsNull()
    {
        var logs = new List<HabitLog>
        {
            CreateLog(DateTime.UtcNow.AddDays(-1)),
            CreateLog(DateTime.UtcNow.AddDays(-2))
        };

        var result = SlipPatternDetectionService.DetectPattern(logs, HabitId, Utc);

        result.Should().BeNull();
    }

    [Fact]
    public void DetectPattern_NoConcentratedDays_ReturnsNull()
    {
        // Create exactly 7 logs, one per distinct day-of-week, so no day reaches threshold of 3
        var logs = new List<HabitLog>();
        // Start from a known Monday and add one log per day for 7 days
        var monday = DateTime.UtcNow.AddDays(-((int)DateTime.UtcNow.DayOfWeek - 1 + 7) % 7);
        for (int i = 0; i < 7; i++)
        {
            logs.Add(CreateLog(monday.AddDays(-i)));
        }

        var result = SlipPatternDetectionService.DetectPattern(logs, HabitId, Utc);

        // With only 1 log per day-of-week, no day reaches the threshold of 3
        result.Should().BeNull();
    }

    [Fact]
    public void DetectPattern_ConcentratedOnOneDay_ReturnsPattern()
    {
        // Create 4 logs all on Mondays (within 60 days)
        var logs = new List<HabitLog>();
        var now = DateTime.UtcNow;
        // Find the most recent Monday
        var daysUntilMonday = ((int)now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7; // Use previous Monday if today is Monday
        var lastMonday = now.AddDays(-daysUntilMonday);

        for (int i = 0; i < 4; i++)
        {
            logs.Add(CreateLog(lastMonday.AddDays(-7 * i)));
        }

        var result = SlipPatternDetectionService.DetectPattern(logs, HabitId, Utc);

        result.Should().NotBeNull();
        result!.HabitId.Should().Be(HabitId);
        result.DayOfWeek.Should().Be(DayOfWeek.Monday);
        result.OccurrenceCount.Should().Be(4);
        result.Confidence.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DetectPattern_MultipleDays_ReturnsStrongestPattern()
    {
        var logs = new List<HabitLog>();
        var now = DateTime.UtcNow;

        // Find specific days
        var daysUntilMonday = ((int)now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;
        var lastMonday = now.AddDays(-daysUntilMonday);

        var daysUntilFriday = ((int)now.DayOfWeek - (int)DayOfWeek.Friday + 7) % 7;
        if (daysUntilFriday == 0) daysUntilFriday = 7;
        var lastFriday = now.AddDays(-daysUntilFriday);

        // 3 Monday logs
        for (int i = 0; i < 3; i++)
            logs.Add(CreateLog(lastMonday.AddDays(-7 * i)));

        // 5 Friday logs (stronger pattern)
        for (int i = 0; i < 5; i++)
            logs.Add(CreateLog(lastFriday.AddDays(-7 * i)));

        var result = SlipPatternDetectionService.DetectPattern(logs, HabitId, Utc);

        result.Should().NotBeNull();
        result!.DayOfWeek.Should().Be(DayOfWeek.Friday);
        result.OccurrenceCount.Should().Be(5);
    }
}
