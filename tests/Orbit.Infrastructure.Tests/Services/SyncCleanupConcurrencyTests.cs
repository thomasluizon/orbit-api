using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class SyncCleanupConcurrencyTests
{
    [PostgresFact]
    public Task Purge_ConcurrentUserUpdate_PreservesCompletionAndOtherChanges() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var (user, completion) = await SeedAsync(db);
            var boundary = new PurgeBoundary();
            using var services = Services(db, boundary);
            var purge = Service(services).PurgeSoftDeletedEntities(default);
            try
            {
                await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await db.Users.Where(item => item.Id == user.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.Name, "Changed concurrently"));
            }
            finally
            {
                boundary.Release.TrySetResult();
            }
            await purge;

            var persisted = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
            persisted.Name.Should().Be("Changed concurrently");
            persisted.LastPurgedCompletionDate.Should().Be(completion);
            (await db.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(0);
            (await db.HabitLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        });

    [PostgresFact]
    public Task Purge_ConcurrentNewerCompletion_DoesNotRegressWatermark() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var (user, completion) = await SeedAsync(db);
            var newerCompletion = completion.AddDays(1);
            var boundary = new PurgeBoundary();
            using var services = Services(db, boundary);
            var purge = Service(services).PurgeSoftDeletedEntities(default);
            try
            {
                await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await db.Users.Where(item => item.Id == user.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.LastPurgedCompletionDate, newerCompletion));
            }
            finally
            {
                boundary.Release.TrySetResult();
            }
            await purge;

            (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id))
                .LastPurgedCompletionDate.Should().Be(newerCompletion);
            (await db.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        });

    [PostgresFact]
    public Task Purge_OverlappingRuns_PreserveNewestCompletion() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var (user, completion) = await SeedAsync(db);
            var boundary = new PurgeBoundary();
            using var firstServices = Services(db, boundary);
            using var secondServices = Services(db);
            var first = Service(firstServices).PurgeSoftDeletedEntities(default);
            try
            {
                await boundary.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await Service(secondServices).RunAsync(default);
            }
            finally
            {
                boundary.Release.TrySetResult();
            }
            await first;

            (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id))
                .LastPurgedCompletionDate.Should().Be(completion);
            (await db.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(0);
            (await db.HabitLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        });

    [PostgresFact]
    public Task Purge_DeleteFails_KeepsCommittedBatchesAndResumes() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var (user, completion) = await SeedAsync(db);
            var goal = Goal.Create(user.Id, "Old goal", 1, "times").Value;
            goal.SoftDelete();
            typeof(Goal).GetProperty(nameof(Goal.DeletedAtUtc))!.SetValue(goal,
                DateTime.UtcNow.AddDays(-AppConstants.MaxSyncWindowDays - AppConstants.SyncCleanupMarginDays - 1));
            db.Goals.Add(goal);
            await db.SaveChangesAsync();
            using var services = Services(db, new RejectGoalDelete());

            var act = () => Service(services).PurgeSoftDeletedEntities(default);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected purge failure");

            (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id))
                .LastPurgedCompletionDate.Should().Be(completion);
            (await db.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(0);
            (await db.HabitLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
            (await db.Goals.IgnoreQueryFilters().CountAsync()).Should().Be(1);

            await Service(services).PurgeSoftDeletedEntities(default);

            (await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id))
                .LastPurgedCompletionDate.Should().Be(completion);
            (await db.Goals.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        });

    private static async Task<(User User, DateOnly Completion)> SeedAsync(OrbitDbContext db)
    {
        var user = User.Create("Purge user", "purge-concurrency@example.test").Value;
        var completion = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Exercise",
            FrequencyUnit.Day, 1, DueDate: completion)).Value;
        habit.Log(completion, advanceDueDate: false);
        habit.SoftDelete(DateTime.UtcNow.AddDays(-AppConstants.MaxSyncWindowDays - AppConstants.SyncCleanupMarginDays - 1));
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (user, completion);
    }

    private static ServiceProvider Services(OrbitDbContext db, params IInterceptor[] interceptors) =>
        new ServiceCollection().AddScoped(_ => new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(db.Database.GetConnectionString(), postgres => postgres.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(interceptors).Options)).BuildServiceProvider();

    private static SyncCleanupService Service(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SyncCleanupService>.Instance);

    private sealed class PurgeBoundary : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"Users\" SET \"LastPurgedCompletionDate\"", StringComparison.Ordinal)
                && Interlocked.Increment(ref _arrivals) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class RejectGoalDelete : DbCommandInterceptor
    {
        private int _rejected;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM \"Goals\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _rejected, 1) == 0)
                throw new InvalidOperationException("Injected purge failure");
            return ValueTask.FromResult(result);
        }
    }
}
