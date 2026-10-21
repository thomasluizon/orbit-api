using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

public class HabitTagUpdateRepositoryTests
{
    [Theory]
    [InlineData("preserve")]
    [InlineData("clear")]
    [InlineData("replace")]
    [InlineData("invalid-tag")]
    [InlineData("invalid-goal")]
    public async Task Update_FreshContextsExerciseAssociationIncludesAndFullReadback(string mode)
    {
        var today = new DateOnly(2026, 4, 3);
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"HabitTags-{Guid.NewGuid()}").Options;
        var user = User.Create("Alex", "habit-tags@example.test").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Original", FrequencyUnit.Day, 1, today,
            ChecklistItems: [new("Same", false), new("Same", true)])).Value;
        var oldTag = Tag.Create(user.Id, "Old", "#ffffff").Value;
        var newTag = Tag.Create(user.Id, "New", "#ffffff").Value;
        var oldGoal = Goal.Create(user.Id, "Old goal", 10, "items").Value;
        var newGoal = Goal.Create(user.Id, "New goal", 10, "items").Value;
        habit.AddTag(oldTag);
        habit.AddGoal(oldGoal);
        await using (var seed = new OrbitDbContext(options))
        {
            seed.Users.Add(user);
            seed.Habits.Add(habit);
            seed.Tags.AddRange(oldTag, newTag);
            seed.Goals.AddRange(oldGoal, newGoal);
            await seed.SaveChangesAsync();
        }

        ChecklistItem[] checklist = [new("Same", true), new("Other", false), new("Same", false)];
        var rejected = mode.StartsWith("invalid", StringComparison.Ordinal);
        await using (var mutation = new OrbitDbContext(options))
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            await using var work = new UnitOfWork(mutation, new DatabaseConnectionSettings());
            var dates = Substitute.For<IUserDateService>();
            dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
            var config = Substitute.For<IAppConfigService>();
            config.GetAsync(AppConfigKeys.MaxTagsPerHabit, AppConstants.MaxTagsPerHabit, Arg.Any<CancellationToken>())
                .Returns(AppConstants.MaxTagsPerHabit);
            var goals = new GenericRepository<Goal>(mutation);
            var completion = new GoalCompletionService(goals, Substitute.For<IGamificationService>(), work, dates);
            var handler = new UpdateHabitCommandHandler(new GenericRepository<Habit>(mutation),
                new GenericRepository<SentReminder>(mutation), goals, Substitute.For<IPayGateService>(), completion,
                dates, work, cache, new GenericRepository<Tag>(mutation), config);
            IReadOnlyList<Guid>? tagIds = mode switch
            {
                "preserve" => null,
                "clear" => [],
                "invalid-tag" => [newTag.Id, Guid.NewGuid()],
                _ => [newTag.Id]
            };
            var command = new UpdateHabitCommand(user.Id, habit.Id, "Changed", "Details", FrequencyUnit.Day, 1,
                DueDate: today.AddDays(1), GoalIds: [mode == "invalid-goal" ? Guid.NewGuid() : newGoal.Id], TagIds: tagIds,
                Options: new(ChecklistItems: checklist, DueTime: new TimeOnly(10, 0), ReminderEnabled: true, ReminderTimes: [30]));

            var result = await handler.Handle(command, CancellationToken.None);
            result.IsSuccess.Should().Be(!rejected);
        }

        await using var readback = new OrbitDbContext(options);
        var saved = await readback.Habits.Include(h => h.Tags).Include(h => h.Goals).SingleAsync(h => h.Id == habit.Id);
        saved.Title.Should().Be(rejected ? "Original" : "Changed");
        saved.Description.Should().Be(rejected ? null : "Details");
        saved.DueDate.Should().Be(rejected ? today : today.AddDays(1));
        saved.DueTime.Should().Be(rejected ? null : new TimeOnly(10, 0));
        saved.ReminderEnabled.Should().Be(!rejected);
        saved.ReminderTimes.Should().Equal(rejected ? habit.ReminderTimes : new[] { 30 });
        saved.ChecklistItems.Should().Equal(rejected ? habit.ChecklistItems : checklist);
        saved.Goals.Select(g => g.Id).Should().Equal(rejected ? oldGoal.Id : newGoal.Id);
        saved.Tags.Select(t => t.Id).Should().Equal(mode switch
        {
            "clear" => [],
            "replace" => [newTag.Id],
            _ => [oldTag.Id]
        });
        (await readback.Goals.Include(g => g.Habits).SingleAsync(g => g.Id == oldGoal.Id))
            .Habits.Should().HaveCount(rejected ? 1 : 0);
        (await readback.Goals.Include(g => g.Habits).SingleAsync(g => g.Id == newGoal.Id))
            .Habits.Should().HaveCount(rejected ? 0 : 1);
    }
}
