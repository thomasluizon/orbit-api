using FluentAssertions;
using Orbit.Application.Gamification.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class TimeOfDayAchievementCountsTests
{
    [Theory]
    [InlineData("America/New_York", 1, 1)]
    [InlineData("Asia/Kolkata", 1, 0)]
    public async Task SqlMinuteGroups_PreserveDaylightSavingAndHalfHourBandEdges(string zoneId, int early, int night)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Bands", "bands@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read",
            FrequencyUnit.Day, 1, new DateOnly(2026, 10, 1))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        DateTime[] instants = [new(2026, 10, 31, 11, 30, 0, DateTimeKind.Utc),
            new(2026, 11, 1, 11, 30, 0, DateTimeKind.Utc),
            new(2026, 11, 1, 2, 30, 0, DateTimeKind.Utc),
            new(2026, 11, 2, 2, 30, 0, DateTimeKind.Utc),
            new(2026, 11, 2, 1, 15, 0, DateTimeKind.Utc),
            new(2026, 11, 2, 1, 45, 0, DateTimeKind.Utc)];
        db.HabitLogs.AddRange(instants.Select((instant, index) => HabitLog.FromScheduleRead(Guid.NewGuid(),
            habit.Id, DateOnly.FromDateTime(instant), 1, index, instant)));
        await db.SaveChangesAsync();
        var counts = await TimeOfDayAchievementCounts.ReadAsync(new GenericRepository<HabitLog>(db), [habit.Id],
            new HashSet<string>(), TimeZoneInfo.FindSystemTimeZoneById(zoneId), default, new Clock());
        counts.Should().Be(new TimeOfDayCounts(early, night));
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    }
}
