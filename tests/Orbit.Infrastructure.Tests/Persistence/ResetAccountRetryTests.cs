using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Events;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ResetAccountRetryTests
{
    [Fact]
    public async Task Handle_DeadlockOnFirstSave_RetriesDeletionsAndPersistsUserReset()
    {
        var failure = new DeadlockOnFirstSave();
        using var factory = new SqliteOrbitDbContextFactory(
            dependencies => new NpgsqlRetryingExecutionStrategy(dependencies, 1, TimeSpan.Zero, null), failure);
        var context = factory.Context;
        var today = new DateOnly(2026, 6, 15);
        var user = User.Create("Test User", "reset@example.com").Value;
        user.AddXp(123);
        user.SetLevel(3);
        user.SetStreakState(5, 10, today);
        user.CompleteOnboarding();
        user.SetGoogleTokens("access-token", "refresh-token");
        user.EnableCalendarAutoSync().IsSuccess.Should().BeTrue();
        context.Users.Add(user);
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Habit", FrequencyUnit.Day, 1, today)).Value;
        context.Habits.Add(habit);
        context.UserFacts.Add(UserFact.Create(user.Id, "likes mornings", "preference").Value);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        failure.Enabled = true;

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var events = Substitute.For<IAccountEventCollector>();
        var handler = new ResetAccountCommandHandler(
            new GenericRepository<User>(context),
            new AccountResetRepository(context, events),
            new UnitOfWork(context, new DatabaseConnectionSettings(), events),
            dates,
            cache);

        var result = await handler.Handle(new ResetAccountCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        failure.Attempts.Should().Be(2);
        events.Received(2).MarkResync(user.Id);
        events.Received(1).Clear();
        await using var verification = factory.CreateContext();
        (await verification.Habits.IgnoreQueryFilters().CountAsync(h => h.UserId == user.Id)).Should().Be(0);
        (await verification.UserFacts.IgnoreQueryFilters().CountAsync(f => f.UserId == user.Id)).Should().Be(0);
        var persisted = await verification.Users.SingleAsync(u => u.Id == user.Id);
        using (new FluentAssertions.Execution.AssertionScope())
        {
            persisted.TotalXp.Should().Be(0);
            persisted.Level.Should().Be(1);
            persisted.CurrentStreak.Should().Be(0);
            persisted.LongestStreak.Should().Be(0);
            persisted.LastActiveDate.Should().BeNull();
            persisted.HasCompletedOnboarding.Should().BeFalse();
            persisted.GoogleAccessToken.Should().BeNull();
            persisted.GoogleRefreshToken.Should().BeNull();
            persisted.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
        }
    }

    private sealed class DeadlockOnFirstSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && ++Attempts == 1)
                throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);

            return ValueTask.FromResult(result);
        }
    }
}
