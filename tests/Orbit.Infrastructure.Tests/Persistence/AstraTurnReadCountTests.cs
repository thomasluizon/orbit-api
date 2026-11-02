using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AstraTurnReadCountTests
{
    [Fact]
    public async Task MutationTags_ProjectRealOwnedLinksWithoutMaterializingHabits()
    {
        var materializations = new HabitMaterializations();
        using var factory = new SqliteOrbitDbContextFactory(materializations);
        var db = factory.Context;
        var user = User.Create("User", "tags@example.com").Value;
        var other = User.Create("Other", "other-tags@example.com").Value;
        var today = new DateOnly(2026, 4, 3);
        var selected = Habit.Create(new(HabitSchedule.Recurring, user.Id, "Read", FrequencyUnit.Day, 1, today)).Value;
        selected.AddTag(Tag.Create(user.Id, "Health", "#123456").Value);
        var untagged = Habit.Create(new(HabitSchedule.Recurring, user.Id, "Walk", FrequencyUnit.Day, 1, today)).Value;
        var foreign = Habit.Create(new(HabitSchedule.Recurring, other.Id, "Work", FrequencyUnit.Day, 1, today)).Value;
        foreign.AddTag(Tag.Create(other.Id, "Work", "#123456").Value);
        db.Users.AddRange(user, other);
        db.Habits.AddRange(selected, untagged, foreign);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        materializations.Count = 0;

        var rows = await ChatHabitTags.Select(db.Habits.AsNoTracking().Where(habit => habit.UserId == user.Id)).ToArrayAsync();

        rows.Should().ContainSingle().Which.HabitId.Should().Be(selected.Id);
        rows[0].Tags.Should().ContainSingle().Which.Name.Should().Be("Health");
        rows[0].Tags.Should().OnlyContain(tag => tag.UserId == user.Id);
        materializations.Count.Should().Be(0);
    }

    private sealed class HabitMaterializations : IMaterializationInterceptor
    {
        internal int Count { get; set; }
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is Habit) Count++;
            return entity;
        }
    }

    [Fact]
    public async Task FiveHundredHabits_ThreeReads_FlagsAndQuota_LoadTheSetAndUserOnce()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var user = User.Create("User", "user@example.com").Value;
        db.Users.Add(user);
        var today = new DateOnly(2026, 4, 3);
        db.Habits.AddRange(Enumerable.Range(0, 500).Select(i => Habit.Create(new HabitCreateParams(
            HabitSchedule.Recurring, user.Id, "Habit " + i, FrequencyUnit.Day, 1, today)).Value));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        counter.Reset();
        var users = new GenericRepository<User>(db);
        var habits = new GenericRepository<Habit>(db);
        using var turn = new ChatTurnReadScope(user.Id, await users.GetByIdAsync(user.Id));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var flags = new FeatureFlagService(db, cache, Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build());
        turn.EnabledFlags = await flags.GetEnabledKeysForUserAsync(user.Id);
        await flags.GetEnabledKeysForUserAsync(user.Id);
        flags.ResolveForUser(user.Id);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
        var quota = new PayGateService(habits, users, config, dates, flags);
        (await quota.TryConsumeAiMessage(user.Id, new UnitOfWork(db, new DatabaseConnectionSettings()))).IsSuccess.Should().BeTrue();
        await turn.ReadHabitsAsync(habits, today, false, default);
        var tool = new QueryHabitsTool(habits, users, dates, Substitute.For<IHabitScheduleLogReader>());
        for (var i = 0; i < 3; i++)
        {
            var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { offset = i * 10, limit = 10 }), user.Id, default);
            result.Success.Should().BeTrue();
            ((HabitListCard)result.Payload!).Items.Should().HaveCount(10);
        }
        counter.Commands.Count(command => command.Sql.Contains(ChatHabitCandidate.QueryTag, StringComparison.Ordinal)).Should().Be(1);
        counter.Commands.Count(command => command.Sql.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)
            && command.Sql.Contains("FROM \"Users\"", StringComparison.Ordinal)).Should().Be(1);
        counter.Commands.Where(command => command.Sql.Contains("FROM \"Habits\"", StringComparison.Ordinal)
            && !command.Sql.Contains(ChatHabitCandidate.QueryTag, StringComparison.Ordinal))
            .Should().OnlyContain(command => command.Rows <= 10, "relation hydration must stay within the SQL page");
    }
}
