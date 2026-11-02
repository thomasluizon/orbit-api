using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

public sealed class RecurringJobConcurrencyTests
{
    [Theory]
    [InlineData("habit-due-date-advancement")]
    [InlineData("streak-goal-sync")]
    [InlineData("sync-cleanup")]
    public async Task Run_TwoScopes_SecondOccurrenceSkipsActualJobWhileFirstIsActive(string name)
    {
        var barrier = new WorkBarrier(name);
        using var factory = new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), $"job-overlap-{Guid.NewGuid():N}.db"), barrier);
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        var user = User.Create("Worker", "worker@example.com").Value;
        user.SetTimeZone("UTC");
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Habit", FrequencyUnit.Day, 1,
            DueDate: today.AddDays(-5), IsBadHabit: true)).Value;
        if (name == "sync-cleanup")
        {
            habit.SoftDelete();
            typeof(Habit).GetProperty(nameof(Habit.DeletedAtUtc))!.SetValue(habit, TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-40));
        }
        factory.Context.AddRange(user, habit, Goal.Create(new Goal.CreateGoalParams(user.Id, "Streak", 10, "days", Type: GoalType.Streak)).Value);
        await factory.Context.SaveChangesAsync();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<string?>(), user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var completion = Substitute.For<IGoalCompletionService>();
        completion.SyncDerivedGoalsAsync(user.Id, Arg.Any<IReadOnlyCollection<Guid>>(), today, true,
            Arg.Any<CancellationToken>(), Arg.Any<DerivedGoalPreferences?>()).Returns(Array.Empty<GoalCompletionUpdate>());
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext()).AddSingleton(dates)
            .AddSingleton(completion).BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var config = new ConfigurationBuilder().Build();
        IScheduledJob Job() => name switch
        {
            "habit-due-date-advancement" => new HabitDueDateAdvancementService(scopes, NullLogger<HabitDueDateAdvancementService>.Instance, config, TimeProvider.System),
            "streak-goal-sync" => new StreakGoalSyncService(scopes, NullLogger<StreakGoalSyncService>.Instance, config),
            _ => new SyncCleanupService(scopes, NullLogger<SyncCleanupService>.Instance, config)
        };
        var leases = new TestScheduledJobLeaseProvider();
        var health = new BackgroundServiceHealthCheck(scopes, [Job()], TimeProvider.System, NullLogger<BackgroundServiceHealthCheck>.Instance);
        var first = new ScheduledJobRunner([Job()], leases, health, config);
        var second = new ScheduledJobRunner([Job()], leases, health, config);
        barrier.Enabled = true;

        var run = first.RunAsync(name, default);
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await second.RunAsync(name, default).WaitAsync(TimeSpan.FromSeconds(1));
            barrier.Attempts.Should().Be(1);
        }
        finally { barrier.Release.TrySetResult(); }
        await run;

        using var read = factory.CreateContext();
        (await read.BackgroundJobHeartbeats.SingleAsync(row => row.JobName == name)).LastSuccessfulTickAtUtc.Should().NotBeNull();
        if (name == "habit-due-date-advancement") (await read.Habits.SingleAsync()).NextDueDate.Should().Be(today);
        if (name == "sync-cleanup") (await read.Habits.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
        if (name == "streak-goal-sync")
            await completion.Received(1).SyncDerivedGoalsAsync(user.Id, Arg.Any<IReadOnlyCollection<Guid>>(), today, true,
                Arg.Any<CancellationToken>(), Arg.Any<DerivedGoalPreferences?>());
    }

    private sealed class WorkBarrier(string name) : DbCommandInterceptor
    {
        internal bool Enabled { get; set; }
        internal int Attempts { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Observe(DbCommand command, CancellationToken ct)
        {
            if (!Enabled) return;
            var sql = command.CommandText;
            var work = name switch
            {
                "habit-due-date-advancement" => sql.StartsWith("SELECT", StringComparison.Ordinal) && sql.Contains("NextDueDate", StringComparison.Ordinal),
                "streak-goal-sync" => sql.StartsWith("SELECT", StringComparison.Ordinal) && sql.Contains("Goals", StringComparison.Ordinal),
                _ => sql.StartsWith("WITH RECURSIVE", StringComparison.Ordinal)
            };
            if (!work) return;
            Attempts++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { await Observe(command, cancellationToken); return result; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { await Observe(command, cancellationToken); return result; }
    }
}
