using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitTagUpdateRelationalTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);
    private static readonly ChecklistItem[] Checklist =
        [new("Same", true), new("Other", false), new("Same", false)];

    [Fact]
    public Task Update_Success_CommitsCompleteStateAfterDerivedGoalFlush() => ExerciseAsync("success");

    [Theory]
    [InlineData("storage")]
    [InlineData("cancellation")]
    public Task Update_FailureAfterFlush_RollsBackCompleteState(string failure) => ExerciseAsync(failure);

    private static async Task ExerciseAsync(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new FlushProbe(outcome == "cancellation" ? cancellation : null);
        using var factory = new SqliteOrbitDbContextFactory(probe);
        var seed = factory.Context;
        var user = User.Create("Alex", "habit-rollback@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Original", FrequencyUnit.Day, 1, Today,
            DueTime: new TimeOnly(9, 0), ReminderEnabled: true, ReminderTimes: [15],
            ChecklistItems: [new("Same", false), new("Same", true), new("Other", false)])).Value;
        var oldTag = Tag.Create(user.Id, "Old", "#ffffff").Value;
        var newTag = Tag.Create(user.Id, "New", "#ffffff").Value;
        var oldGoal = Goal.Create(user.Id, "Old goal", 10, "items").Value;
        var newGoal = Goal.Create(user.Id, "New goal", 10, "items").Value;
        newGoal.UpdateProgress(3).IsSuccess.Should().BeTrue();
        habit.AddTag(oldTag);
        habit.AddGoal(oldGoal);
        seed.Users.Add(user);
        seed.Habits.Add(habit);
        seed.Tags.AddRange(oldTag, newTag);
        seed.Goals.AddRange(oldGoal, newGoal);
        seed.SentReminders.AddRange(SentReminder.Create(habit.Id, Today, 15),
            SentReminder.Create(habit.Id, Today.AddDays(-1), 15));
        await seed.SaveChangesAsync();
        await using var beforeContext = factory.CreateContext();
        var before = await ReadAsync(beforeContext, habit.Id);

        if (outcome == "storage")
            await seed.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TRIGGER "RejectDerivedGoalUpdate" BEFORE UPDATE ON "Goals"
                WHEN OLD."Title" = 'New goal'
                BEGIN SELECT RAISE(ABORT, 'Injected derived goal storage failure'); END;
                """);

        await using var mutation = factory.CreateContext();
        probe.Target = mutation;
        probe.HabitId = habit.Id;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheKey = $"goal-review:{user.Id}:en";
        cache.Set(cacheKey, "cached");
        var work = new UnitOfWork(mutation, new DatabaseConnectionSettings());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        var config = Substitute.For<IAppConfigService>();
        Guid? preparedTransaction = null;
        config.GetAsync(AppConfigKeys.MaxTagsPerHabit, AppConstants.MaxTagsPerHabit, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                preparedTransaction = mutation.Database.CurrentTransaction?.TransactionId;
                return AppConstants.MaxTagsPerHabit;
            });
        var goals = new GenericRepository<Goal>(mutation);
        var completion = new GoalCompletionService(goals, Substitute.For<IGamificationService>(), work, dates);
        var handler = new UpdateHabitCommandHandler(new GenericRepository<Habit>(mutation),
            new GenericRepository<SentReminder>(mutation), goals, Substitute.For<IPayGateService>(), completion,
            dates, work, cache, new GenericRepository<Tag>(mutation), config);
        var command = new UpdateHabitCommand(user.Id, habit.Id, "Changed", "Details", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(1), GoalIds: [newGoal.Id], TagIds: [newTag.Id], Emoji: "book",
            Options: new(ChecklistItems: Checklist, DueTime: new TimeOnly(10, 0),
                ReminderEnabled: true, ReminderTimes: [30], EndDate: Today.AddDays(5)));

        var act = () => handler.Handle(command, cancellation.Token);
        if (outcome == "success")
            (await act()).IsSuccess.Should().BeTrue();
        else if (outcome == "storage")
            (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException
                .Should().BeOfType<SqliteException>().Which.Message.Should().Contain("Injected derived goal storage failure");
        else
            await act.Should().ThrowAsync<OperationCanceledException>();

        await using var readback = factory.CreateContext();
        var after = await ReadAsync(readback, habit.Id);
        using var assertions = new AssertionScope();
        if (outcome == "success")
        {
            AssertUpdated(after.Habit, newTag.Id, newGoal.Id);
            after.Goals.Single(g => g.Id == newGoal.Id).CurrentValue.Should().Be(0);
            after.Goals.Single(g => g.Id == newGoal.Id).Habits.Select(h => h.Id).Should().Equal(habit.Id);
            after.Goals.Single(g => g.Id == oldGoal.Id).Habits.Should().BeEmpty();
            after.Reminders.Should().ContainSingle(r => r.Date == Today.AddDays(-1));
            cache.TryGetValue(cacheKey, out _).Should().BeFalse();
        }
        else
        {
            after.Habit.Title.Should().Be(before.Habit.Title);
            after.Habit.ChecklistItems.Should().Equal(before.Habit.ChecklistItems);
            after.Habit.Tags.Select(t => t.Id).Should().Equal(oldTag.Id);
            after.Habit.Goals.Select(g => g.Id).Should().Equal(oldGoal.Id);
            string.Equals(after.Snapshot, before.Snapshot, StringComparison.Ordinal).Should().BeTrue(
                "every persisted field, association, goal and reminder must roll back");
            mutation.ChangeTracker.Entries().Should().BeEmpty();
            cache.TryGetValue(cacheKey, out _).Should().BeTrue();
        }

        probe.Starts.Should().Be(2, "derived goal persistence follows the first completed habit save");
        probe.Flushes.Should().HaveCount(outcome == "success" ? 2 : 1);
        preparedTransaction.Should().NotBeNull("association preparation must already be inside the owned transaction");
        foreach (var flush in probe.Flushes)
        {
            flush.Rows.Should().BePositive();
            flush.TransactionId.Should().Be(preparedTransaction);
            AssertUpdated(flush.State.Habit, newTag.Id, newGoal.Id);
            flush.State.Reminders.Should().ContainSingle(r => r.Date == Today.AddDays(-1));
        }
        probe.Flushes[0].State.Goals.Single(g => g.Id == newGoal.Id).CurrentValue.Should().Be(3,
            "the first actual SQL flush precedes derived goal progress persistence");
        mutation.Database.CurrentTransaction.Should().BeNull();
    }

    private static void AssertUpdated(Habit habit, Guid tagId, Guid goalId)
    {
        habit.Title.Should().Be("Changed");
        habit.Description.Should().Be("Details");
        habit.DueDate.Should().Be(Today.AddDays(1));
        habit.DueTime.Should().Be(new TimeOnly(10, 0));
        habit.EndDate.Should().Be(Today.AddDays(5));
        habit.Emoji.Should().Be("book");
        habit.ReminderEnabled.Should().BeTrue();
        habit.ReminderTimes.Should().Equal(30);
        habit.ChecklistItems.Should().Equal(Checklist);
        habit.Tags.Select(t => t.Id).Should().Equal(tagId);
        habit.Goals.Select(g => g.Id).Should().Equal(goalId);
    }

    private static async Task<Readback> ReadAsync(OrbitDbContext context, Guid habitId)
    {
        var habit = await context.Habits.AsNoTracking().Include(h => h.Tags).Include(h => h.Goals)
            .SingleAsync(h => h.Id == habitId);
        var goals = await context.Goals.AsNoTracking().Include(g => g.Habits).OrderBy(g => g.Title).ToArrayAsync();
        var reminders = await context.SentReminders.AsNoTracking().Where(r => r.HabitId == habitId)
            .OrderBy(r => r.Date).ToArrayAsync();
        var snapshot = JsonSerializer.Serialize(new
        {
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days,
            habit.IsBadHabit,
            habit.DueDate,
            habit.DueTime,
            habit.DueEndTime,
            habit.ReminderEnabled,
            habit.ReminderTimes,
            habit.ScheduledReminders,
            habit.RelativeReminders,
            habit.SlipAlertEnabled,
            habit.ChecklistItems,
            habit.IsGeneral,
            habit.EndDate,
            habit.IsFlexible,
            habit.Emoji,
            habit.IntervalWeeks,
            habit.StartDate,
            habit.IsCompleted,
            habit.UpdatedAtUtc,
            habit.ReminderProbeVersion,
            Tags = habit.Tags.Select(t => t.Id).Order(),
            Goals = habit.Goals.Select(g => g.Id).Order(),
            GoalState = goals.Select(g => new
            {
                g.Id,
                g.CurrentValue,
                g.Status,
                g.UpdatedAtUtc,
                g.CompletedAtUtc,
                Habits = g.Habits.Select(h => h.Id).Order()
            }),
            Reminders = reminders.Select(r => new
            {
                r.Id,
                r.Date,
                r.MinutesBefore,
                r.ReminderTimeUtc,
                r.When,
                r.SentAtUtc
            })
        });
        return new(habit, goals, reminders, snapshot);
    }

    private sealed record Readback(Habit Habit, Goal[] Goals, SentReminder[] Reminders, string Snapshot);
    private sealed record Flush(int Rows, Guid? TransactionId, Readback State);

    private sealed class FlushProbe(CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        public OrbitDbContext? Target { get; set; }
        public Guid HabitId { get; set; }
        public int Starts { get; private set; }
        public List<Flush> Flushes { get; } = [];

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(eventData.Context, Target) && ++Starts == 2 && cancellation is not null)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return result;
        }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (Target is { } target && ReferenceEquals(eventData.Context, target))
                Flushes.Add(new(result, target.Database.CurrentTransaction?.TransactionId,
                    await ReadAsync(target, HabitId)));
            return result;
        }
    }
}
