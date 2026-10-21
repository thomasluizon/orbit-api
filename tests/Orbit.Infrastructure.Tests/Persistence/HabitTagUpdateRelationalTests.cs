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
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Infrastructure.Tests.Controllers;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Update_Success_CommitsCompleteStateAfterDerivedGoalFlush(bool clearParent) => ExerciseAsync("success", clearParent);

    [Theory]
    [InlineData("storage", false)]
    [InlineData("storage", true)]
    [InlineData("cancellation", false)]
    [InlineData("cancellation", true)]
    public Task Update_FailureAfterFlush_RollsBackCompleteState(string failure, bool clearParent) => ExerciseAsync(failure, clearParent);

    private static async Task ExerciseAsync(string outcome, bool clearParent)
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new FlushProbe(outcome == "cancellation" ? cancellation : null);
        using var factory = new SqliteOrbitDbContextFactory(probe);
        var seed = factory.Context;
        var user = User.Create("Alex", "habit-rollback@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Original", FrequencyUnit.Day, 1, Today,
            DueTime: new TimeOnly(9, 0), ReminderEnabled: true, ReminderTimes: [15],
            ChecklistItems: [new("Same", false), new("Same", true), new("Other", false)])).Value;
        habit.Log(Today, advanceDueDate: false).IsSuccess.Should().BeTrue();
        habit.UpdateChecklist([new("Same", false), new("Same", true), new("Other", false)]);
        var parent = Habit.Create(new HabitCreateParams(user.Id, "Parent", FrequencyUnit.Day, 1, Today)).Value;
        if (clearParent) habit.SetParentHabitId(parent.Id);
        Guid? finalParentId = clearParent ? null : parent.Id;
        var oldTag = Tag.Create(user.Id, "Old", "#ffffff").Value;
        var newTag = Tag.Create(user.Id, "New", "#ffffff").Value;
        var oldGoal = Goal.Create(user.Id, "Old goal", 10, "items").Value;
        var newGoal = Goal.Create(user.Id, "New goal", 10, "items").Value;
        newGoal.UpdateProgress(3).IsSuccess.Should().BeTrue();
        habit.AddTag(oldTag);
        habit.AddGoal(oldGoal);
        seed.Users.Add(user);
        seed.Habits.AddRange(habit, parent);
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
        config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>())
            .Returns(AppConstants.MaxHabitDepth);
        Guid? preparedTransaction = null;
        config.GetAsync(AppConfigKeys.MaxTagsPerHabit, AppConstants.MaxTagsPerHabit, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                preparedTransaction = mutation.Database.CurrentTransaction?.TransactionId;
                return AppConstants.MaxTagsPerHabit;
            });
        var goals = new GenericRepository<Goal>(mutation);
        var completion = new GoalCompletionService(goals, Substitute.For<IGamificationService>(), work, dates);
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanCreateHabits(user.Id, 1, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var handler = new UpdateHabitCommandHandler(new GenericRepository<Habit>(mutation),
            new GenericRepository<SentReminder>(mutation), goals, payGate, completion,
            dates, work, cache, new GenericRepository<Tag>(mutation), config);
        var command = new UpdateHabitCommand(user.Id, habit.Id, "Changed", "Details", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(1), ParentId: clearParent ? null : parent.Id, ClearParent: clearParent, GoalIds: [newGoal.Id], TagIds: [newTag.Id], Emoji: "book",
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
            AssertUpdated(after.Habit, newTag.Id, newGoal.Id, finalParentId);
            after.Goals.Single(g => g.Id == newGoal.Id).CurrentValue.Should().Be(0);
            after.Goals.Single(g => g.Id == newGoal.Id).Habits.Select(h => h.Id).Should().Equal(habit.Id);
            after.Goals.Single(g => g.Id == oldGoal.Id).Habits.Should().BeEmpty();
            after.Tags.Single(t => t.Id == newTag.Id).Habits.Select(h => h.Id).Should().Equal(habit.Id);
            after.Tags.Single(t => t.Id == oldTag.Id).Habits.Should().BeEmpty();
            after.Habit.Logs.Select(l => l.Id).Should().Equal(before.Habit.Logs.Select(l => l.Id));
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
            AssertUpdated(flush.State.Habit, newTag.Id, newGoal.Id, finalParentId);
            flush.State.Reminders.Should().ContainSingle(r => r.Date == Today.AddDays(-1));
        }
        probe.Flushes[0].State.Goals.Single(g => g.Id == newGoal.Id).CurrentValue.Should().Be(3,
            "the first actual SQL flush precedes derived goal progress persistence");
        mutation.Database.CurrentTransaction.Should().BeNull();
        var expectedParentId = outcome == "success" ? finalParentId : before.Habit.ParentHabitId;
        var ordinary = await new GetHabitByIdQueryHandler(new GenericRepository<Habit>(readback),
            new GenericRepository<HabitLog>(readback), dates).Handle(new(user.Id, habit.Id), default);
        var full = await new GetHabitFullDetailQueryHandler(new GenericRepository<Habit>(readback),
            new GenericRepository<HabitLog>(readback), new GenericRepository<User>(readback), dates)
            .Handle(new(user.Id, habit.Id), default);
        ordinary.IsSuccess.Should().BeTrue();
        full.IsSuccess.Should().BeTrue();
        foreach (var detail in new[] { ordinary.Value, full.Value.Habit })
        {
            detail.ParentId.Should().Be(expectedParentId);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(detail, ReminderInputContractTests.MvcOptions()));
            var value = json.RootElement.GetProperty("parentId");
            if (expectedParentId.HasValue) value.GetGuid().Should().Be(expectedParentId.Value);
            else value.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    private static void AssertUpdated(Habit habit, Guid tagId, Guid goalId, Guid? parentId)
    {
        habit.ParentHabitId.Should().Be(parentId);
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
        var habit = await context.Habits.AsNoTracking().Include(h => h.Tags).Include(h => h.Goals).Include(h => h.Logs)
            .SingleAsync(h => h.Id == habitId);
        var goals = await context.Goals.AsNoTracking().Include(g => g.Habits).OrderBy(g => g.Title).ToArrayAsync();
        var tags = await context.Tags.AsNoTracking().Include(t => t.Habits).OrderBy(t => t.Name).ToArrayAsync();
        var reminders = await context.SentReminders.AsNoTracking().Where(r => r.HabitId == habitId)
            .OrderBy(r => r.Date).ToArrayAsync();
        var snapshot = JsonSerializer.Serialize(new
        {
            habit.ParentHabitId,
            habit.Schedule,
            habit.ScheduledStartDate,
            habit.OriginalDayOfMonth,
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
            Logs = habit.Logs.Select(l => new { l.Id, l.Date, l.Value, l.IsDeleted, l.CreatedAtUtc, l.UpdatedAtUtc }),
            TagState = tags.Select(t => new { t.Id, Habits = t.Habits.Select(h => h.Id).Order() }),
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
        return new(habit, goals, tags, reminders, snapshot);
    }

    private sealed record Readback(Habit Habit, Goal[] Goals, Tag[] Tags, SentReminder[] Reminders, string Snapshot);
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
