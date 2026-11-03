using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedReadStateBackfillTests
{
    [PostgresFact]
    public Task Backfill_PostgresRunsAcrossBatchBoundaryAndCompletesOnce() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var user = User.Create("Postgres backfill", "postgres-backfill@example.test").Value;
            user.SetTimeZone("UTC");
            var today = new DateOnly(2026, 6, 30);
            db.Users.Add(user);
            for (var index = 0; index < 101; index++)
                db.Habits.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
                    user.Id, $"Read {index}", FrequencyUnit.Day, 1, today)).Value);
            await db.SaveChangesAsync();
            await db.Habits.ExecuteUpdateAsync(update => update.SetProperty(row => row.LongestRecordedStreak, (int?)null));
            db.ChangeTracker.Clear();
            var dates = Substitute.For<IUserDateService>();
            dates.GetUserTodayAsync("UTC", user.Id, Arg.Any<CancellationToken>()).Returns(today);
            var backfill = new BoundedReadStateBackfill(db, dates);
            await backfill.RunAsync();
            (await db.Habits.CountAsync(row => row.LongestRecordedStreak != null)).Should().Be(101);
            await backfill.RunAsync();
            (await db.AppConfigs.CountAsync(row => row.Key == BoundedReadStateBackfill.CompletionKey)).Should().Be(1);
            db.Database.HasPendingModelChanges().Should().BeFalse();
        });

    [Fact]
    public void PendingBatch_TranslatesWithNpgsqlForInitialAndContinuationCursors()
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var backfill = new BoundedReadStateBackfill(db, Substitute.For<IUserDateService>());
        foreach (var cursor in new Guid?[] { null, Guid.NewGuid() })
        {
            var sql = backfill.PendingHabits(cursor).Select(habit => new { habit.Id, habit.UserId }).ToQueryString();
            sql.Should().Contain("ORDER BY").And.Contain("LIMIT").And.Contain("IS NULL");
            if (cursor.HasValue) sql.Should().Contain(cursor.Value.ToString());
        }
    }

    [Fact]
    public async Task Backfill_IsIdempotentAndSkipsHabitReadsAfterCompletion()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var today = new DateOnly(2026, 6, 30);
        var user = User.Create("Backfill", "backfill@example.test").Value;
        user.SetTimeZone("UTC");
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, "Read", FrequencyUnit.Day, 1, today.AddDays(-2))).Value;
        for (var day = today.AddDays(-2); day <= today; day = day.AddDays(1))
            habit.Log(day, advanceDueDate: false).IsSuccess.Should().BeTrue();
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        await db.Habits.ExecuteUpdateAsync(update => update.SetProperty(row => row.LongestRecordedStreak, (int?)null));
        db.ChangeTracker.Clear();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync("UTC", user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var backfill = new BoundedReadStateBackfill(db, dates);
        await backfill.RunAsync();
        var longest = await db.Habits.Select(row => row.LongestRecordedStreak).SingleAsync();
        longest.Should().Be(3);
        counter.Reset();
        await backfill.RunAsync();
        counter.Commands.Should().OnlyContain(command => !command.Sql.Contains("Habits", StringComparison.Ordinal));
        (await db.AppConfigs.CountAsync(config => config.Key == BoundedReadStateBackfill.CompletionKey)).Should().Be(1);
    }

    [Fact]
    public async Task HostedBackfill_FailureIsLoggedAndNextAttemptRetries()
    {
        var failure = new FailFirstRead();
        using var factory = new SqliteOrbitDbContextFactory(failure);
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext())
            .AddSingleton(Substitute.For<IUserDateService>()).AddScoped<BoundedReadStateBackfill>().BuildServiceProvider();
        var logger = Substitute.For<ILogger<BoundedReadStateBackfillHostedService>>();
        var worker = new BoundedReadStateBackfillHostedService(provider.GetRequiredService<IServiceScopeFactory>(), logger);
        failure.Enabled = true;
        (await worker.TryRunAsync(default)).Should().BeFalse();
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
        (await worker.TryRunAsync(default)).Should().BeTrue();
        (await factory.Context.AppConfigs.AnyAsync(config => config.Key == BoundedReadStateBackfill.CompletionKey)).Should().BeTrue();
    }

    private sealed class FailFirstRead : DbCommandInterceptor
    {
        internal bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) { Enabled = false; throw new InvalidOperationException("Temporary failure"); }
            return ValueTask.FromResult(result);
        }
    }
}
