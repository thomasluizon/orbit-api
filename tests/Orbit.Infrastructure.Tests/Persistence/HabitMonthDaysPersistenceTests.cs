using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitMonthDaysPersistenceTests
{
    [Fact]
    public async Task MonthDays_RoundTripThroughTheEntityAndTheScheduleProjection()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Month Days", "month-days@example.com").Value;
        var monthly = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Pay bills",
            FrequencyUnit.Month, 1, new DateOnly(2026, 11, 1), MonthDays: [15, 8])).Value;
        var daily = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read",
            FrequencyUnit.Day, 1, new DateOnly(2026, 11, 1))).Value;
        factory.Context.Users.Add(user);
        factory.Context.Habits.AddRange(monthly, daily);
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();

        var loaded = await factory.Context.Habits.AsNoTracking().SingleAsync(h => h.Id == monthly.Id);
        var snapshots = await HabitScheduleProjection.Select(factory.Context.Habits.AsNoTracking()).ToListAsync();

        loaded.MonthDays.Should().Equal(8, 15);
        snapshots.Single(s => s.Id == monthly.Id).MonthDays.Should().Equal(8, 15);
        snapshots.Single(s => s.Id == daily.Id).MonthDays.Should().BeEmpty();
    }
}
