using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class RestoreHabitTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_WithUnavailableTimezoneCache_CompletesHabitGoalsAndStreakInOneTransaction(bool linkedGoal)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Restore", "restore@example.com").Value;
        user.SetTimeZone("America/Sao_Paulo");
        var today = new DateOnly(2026, 10, 29);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Water",
            FrequencyUnit.Day, 1, DueDate: today)).Value;
        habit.SoftDelete(new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc));
        db.AddRange(user, habit);
        if (linkedGoal)
        {
            var goal = Goal.Create(user.Id, "Water goal", 10, "completions").Value;
            goal.AddHabit(habit);
            db.Goals.Add(goal);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var users = new GenericRepository<User>(db);
        var habits = new GenericRepository<Habit>(db);
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var distributed = Substitute.For<IDistributedCache>();
        distributed.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<byte[]?>(_ => throw new TimeoutException("Timezone cache unavailable while a user row is locked."));
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(new DateTimeOffset(2026, 10, 30, 1, 0, 0, TimeSpan.Zero));
        var dates = new UserDateService(users, distributed, clock, work);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var gate = new PayGateService(habits, users, new AppConfigService(db, memory), dates,
            Substitute.For<IFeatureFlagService>());
        var snapshots = new HabitScheduleSnapshotStore(habits);
        var streaks = new UserStreakService(new UserStreakRepositories(users,
            new GenericRepository<HabitLog>(db), new GenericRepository<StreakFreeze>(db)), dates, snapshots);
        var goals = new GoalCompletionService(new GenericRepository<Goal>(db),
            Substitute.For<IGamificationService>(), work, dates);
        var handler = new RestoreHabitCommandHandler(habits, gate, streaks, goals, work, dates, memory);

        var result = await handler.Handle(new RestoreHabitCommand(user.Id, habit.Id), default);

        result.IsSuccess.Should().BeTrue();
        db.Database.CurrentTransaction.Should().BeNull();
        db.ChangeTracker.Clear();
        (await habits.GetByIdAsync(habit.Id))!.IsDeleted.Should().BeFalse();
        await distributed.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await distributed.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }
}
