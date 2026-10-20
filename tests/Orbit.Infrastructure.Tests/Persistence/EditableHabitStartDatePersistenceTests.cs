using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class EditableHabitStartDatePersistenceTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Reanchor_FreshContextSkipsPersistedCompletionAndRetainsHistory(bool terminal, bool syncGoals)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var seed = factory.Context;
        var user = User.Create("Alex", "anchor@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Read", FrequencyUnit.Day, 1,
            Today.AddDays(-10), EndDate: terminal ? Today : null)).Value;
        habit.Log(Today.AddDays(-10), "History").IsSuccess.Should().BeTrue();
        habit.Log(Today).IsSuccess.Should().BeTrue();
        seed.Users.Add(user);
        seed.Habits.Add(habit);
        await seed.SaveChangesAsync();
        var history = habit.Logs.Select(log => (log.Id, log.Date, log.Value, log.IsDeleted)).OrderBy(log => log.Id).ToArray();

        await using (var mutation = factory.CreateContext())
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            await using var work = new UnitOfWork(mutation, new DatabaseConnectionSettings());
            var gate = Substitute.For<IPayGateService>();
            var handler = CreateHandler(mutation, work, cache, gate, user.Id);
            var command = new UpdateHabitCommand(user.Id, habit.Id, "Renamed", null, FrequencyUnit.Day, 1,
                StartDate: Today.AddDays(-1), GoalIds: syncGoals ? [] : null);

            (await handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();

            var loaded = mutation.ChangeTracker.Entries<Habit>().Single().Entity;
            loaded.Logs.Should().ContainSingle().Which.Date.Should().Be(Today);
            loaded.DueDate.Should().Be(Today.AddDays(1));
            loaded.IsCompleted.Should().Be(terminal);
            await gate.DidNotReceive().CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>());
            await work.SaveChangesAsync();
        }

        await using var readback = factory.CreateContext();
        var persisted = await readback.Habits.Include(h => h.Logs).SingleAsync(h => h.Id == habit.Id);
        persisted.StartDate.Should().Be(Today.AddDays(-1));
        persisted.DueDate.Should().Be(Today.AddDays(1));
        persisted.IsCompleted.Should().Be(terminal);
        persisted.Logs.Select(log => (log.Id, log.Date, log.Value, log.IsDeleted)).OrderBy(log => log.Id).Should().Equal(history);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Reanchor_OnlyActualReactivationChecksAllowanceBeforeMutation(bool reactivates, bool allowed)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var seed = factory.Context;
        var user = User.Create("Alex", "reactivation@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Read", FrequencyUnit.Week, 1,
            Today, EndDate: Today.AddDays(3))).Value;
        habit.Log(Today).IsSuccess.Should().BeTrue();
        habit.IsCompleted.Should().BeTrue();
        seed.Users.Add(user);
        seed.Habits.Add(habit);
        await seed.SaveChangesAsync();
        var previous = (habit.StartDate, habit.DueDate, habit.Title, habit.UpdatedAtUtc);
        var newStart = reactivates ? Today.AddDays(1) : Today.AddDays(-7);

        await using (var mutation = factory.CreateContext())
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            await using var work = new UnitOfWork(mutation, new DatabaseConnectionSettings());
            var gate = Substitute.For<IPayGateService>();
            gate.CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>()).Returns(allowed
                ? Result.Success() : Result.Failure("Habit limit reached", ErrorCodes.HabitLimitReached));
            var result = await CreateHandler(mutation, work, cache, gate, user.Id).Handle(
                new UpdateHabitCommand(user.Id, habit.Id, "Renamed", null, FrequencyUnit.Week, 1, StartDate: newStart),
                CancellationToken.None);
            result.IsSuccess.Should().Be(!reactivates || allowed);
            if (reactivates)
                await gate.Received(1).CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>());
            else
                await gate.DidNotReceive().CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>());
        }

        await using var readback = factory.CreateContext();
        var persisted = await readback.Habits.Include(h => h.Logs).SingleAsync(h => h.Id == habit.Id);
        if (reactivates && !allowed)
            (persisted.StartDate, persisted.DueDate, persisted.Title, persisted.UpdatedAtUtc).Should().Be(previous);
        else
        {
            persisted.StartDate.Should().Be(newStart);
            persisted.IsCompleted.Should().Be(!reactivates);
        }
        persisted.Logs.Should().ContainSingle().Which.Id.Should().Be(habit.Logs.Single().Id);
    }

    private static UpdateHabitCommandHandler CreateHandler(OrbitDbContext context, IUnitOfWork work,
        IMemoryCache cache, IPayGateService gate, Guid userId)
    {
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(userId, Arg.Any<CancellationToken>()).Returns(1);
        return new(new GenericRepository<Habit>(context), new GenericRepository<SentReminder>(context),
            new GenericRepository<Goal>(context), gate, Substitute.For<IGoalCompletionService>(), dates, work, cache);
    }
}
