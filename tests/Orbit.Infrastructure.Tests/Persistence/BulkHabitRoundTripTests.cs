using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BulkHabitRoundTripTests
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    [Theory]
    [InlineData("log", 2)]
    [InlineData("skip", 1)]
    [InlineData("update", 1)]
    public async Task Batch_SelectCountIsConstantAndWritesMatchTheMutatedRows(string operation, int writesPerHabit)
    {
        var small = await Run(operation, 2);
        var large = await Run(operation, 50);
        large.Selects.Should().Be(small.Selects);
        (large.Writes - small.Writes).Should().Be(writesPerHabit * 48);
    }

    private static async Task<(int Selects, int Writes)> Run(string operation, int count)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var user = User.Create("Batch user", "batch@example.com").Value;
        db.Users.Add(user);
        var habits = Enumerable.Range(0, count).Select(index => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, user.Id, $"Habit {index}", FrequencyUnit.Day, 1, Today)).Value).ToArray();
        db.Habits.AddRange(habits);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new GenericRepository<Habit>(db);
        var logs = new GenericRepository<HabitLog>(db);
        var notifications = new GenericRepository<Notification>(db);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        counter.Reset();
        if (operation == "log")
        {
            var handler = new BulkLogHabitsCommandHandler(repository, logs,
                new(dates, Substitute.For<IUserStreakService>(), Substitute.For<IGamificationService>(), notifications),
                Substitute.For<IGoalCompletionService>(), unit, cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);
            var result = await handler.Handle(new(user.Id, habits.Select(h => new BulkLogItem(h.Id)).ToList()), default);
            result.Value.Results.Should().OnlyContain(item => item.Status == BulkItemStatus.Success);
        }
        else if (operation == "skip")
        {
            var result = await new BulkSkipHabitsCommandHandler(repository, logs, dates, unit, cache, notifications)
                .Handle(new(user.Id, habits.Select(h => new BulkSkipItem(h.Id)).ToList()), default);
            result.Value.Results.Should().OnlyContain(item => item.Status == BulkItemStatus.Success);
        }
        else
        {
            var result = await new BulkUpdateHabitsCommandHandler(repository, dates, Substitute.For<IPayGateService>(), unit,
                cache, NullLogger<BulkUpdateHabitsCommandHandler>.Instance).Handle(new(user.Id,
                    new(false, habits.Select(h => h.Id).ToList()), new(HasDescription: true, Description: "Changed")), default);
            result.IsSuccess.Should().BeTrue();
            result.Value.AppliedCount.Should().Be(count);
        }
        return (counter.SelectCommandCount, counter.WriteCommandCount);
    }
}
