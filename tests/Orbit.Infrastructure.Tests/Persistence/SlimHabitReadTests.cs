using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class SlimHabitReadTests
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    [Fact]
    public async Task AllLogs_SelectsOnlyResponseFieldsWithConstantRoundTrips()
    {
        var small = await Read(2, widget: false);
        var large = await Read(50, widget: false);
        large.Should().Be(small);
    }

    [Fact]
    public async Task Widget_LoadsCompactRowsWithoutNotesAndWithConstantRoundTrips()
    {
        var small = await Read(2, widget: true);
        var large = await Read(50, widget: true);
        large.Should().Be(small);
    }

    private static async Task<int> Read(int count, bool widget)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var user = User.Create("Read user", "read@example.com").Value;
        db.Users.Add(user);
        for (var i = 0; i < count; i++)
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, $"Habit {i}", FrequencyUnit.Day, 1,
                Today, Description: new string('d', 10000))).Value;
            db.Habits.Add(habit);
            db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, Today, 1, 0, DateTime.UtcNow));
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        counter.Reset();
        var habits = new GenericRepository<Habit>(db);
        var logs = new GenericRepository<HabitLog>(db);
        if (widget)
        {
            var dates = Substitute.For<IUserDateService>();
            dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
            var result = await new GetHabitWidgetQueryHandler(habits, logs, new GenericRepository<User>(db), dates,
                new UnitOfWork(db, new DatabaseConnectionSettings())).Handle(new(user.Id), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            result.Value.Items.Should().HaveCount(count);
        }
        else
        {
            var result = await new GetAllHabitLogsQueryHandler(habits, logs).Handle(new(user.Id, Today, Today), CancellationToken.None);
            result.Value.Should().HaveCount(count);
            result.Value.Values.Should().OnlyContain(values => values.Count == 1 && values[0].Value == 1);
        }
        counter.Commands.Should().OnlyContain(command => !command.Sql.Contains("\"Note\""));
        counter.Commands.Where(command => command.Rows > 0).Should().OnlyContain(command => !command.Sql.Contains("\"Description\""));
        return counter.CommandCount;
    }
}
