using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class QueryGoalsEncryptedSearchTests
{
    [Theory]
    [InlineData("mARatHon", 1, 2)]
    [InlineData("tRAINing", 1, 2)]
    [InlineData("marathon", 99, 0)]
    public async Task Search_DecryptsBeforeFilteringAndPaging(string search, int offset, int expectedCount)
    {
        var encryption = new EncryptionService(Options.Create(new EncryptionSettings { Key = Convert.ToBase64String(new byte[32]) }),
            NullLogger<EncryptionService>.Instance);
        using var database = new SqliteOrbitDbContextFactory(encryption);
        var db = database.Context;
        var user = User.Create("Runner", "search@example.com").Value;
        var other = User.Create("Other", "other-search@example.com").Value;
        var goals = Enumerable.Range(1, 4).Select(index => Goal.Create(new Goal.CreateGoalParams(
            user.Id, "Marathon " + index, 42, "km", "Race training", Position: index)).Value).ToArray();
        var completed = Goal.Create(new Goal.CreateGoalParams(user.Id, "Marathon completed", 42, "km", "Race training", Position: 5)).Value;
        completed.MarkCompleted();
        var deleted = Goal.Create(new Goal.CreateGoalParams(user.Id, "Marathon deleted", 42, "km", "Race training", Position: 6)).Value;
        deleted.SoftDelete();
        var today = new DateOnly(2026, 4, 3);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Morning run", FrequencyUnit.Day, 1, today)).Value;
        goals[1].AddHabit(habit);
        db.Users.AddRange(user, other);
        db.Goals.AddRange(goals);
        db.Goals.AddRange(completed, deleted,
            Goal.Create(new Goal.CreateGoalParams(user.Id, "Read", 10, "books", Position: 0)).Value,
            Goal.Create(new Goal.CreateGoalParams(other.Id, "Marathon foreign", 42, "km", "Race training")).Value);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT Title, Description FROM Goals WHERE Description IS NOT NULL";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().StartWith("enc:");
            reader.GetString(1).Should().StartWith("enc:");
        }
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var syncer = Substitute.For<IGoalProgressReadSyncer>();
        syncer.ComputeFreshValuesForGoalsAsync(user.Id, today, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        var tool = new QueryGoalsTool(new GenericRepository<Goal>(db), dates, syncer);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { search, offset, limit = 2 }), user.Id, default);

        result.Success.Should().BeTrue();
        var card = (GoalListCard)result.Payload!;
        card.TotalCount.Should().Be(4);
        card.Items.Should().HaveCount(expectedCount);
        var expectedIds = goals.Skip(offset).Take(2).Select(goal => goal.Id).ToArray();
        card.Items.Select(item => Guid.Parse(item.Id)).Should().Equal(expectedIds);
        if (expectedCount > 0)
            result.EntityName.Should().Contain("Morning run");
        await syncer.Received(1).ComputeFreshValuesForGoalsAsync(user.Id, today,
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(expectedIds)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task WithoutSearch_KeepsSqlPagingAndRecomputesOnlyThePage(string? search)
    {
        var counter = new CountingDbCommandInterceptor();
        using var database = new SqliteOrbitDbContextFactory(counter);
        var db = database.Context;
        var user = User.Create("Runner", "page@example.com").Value;
        db.Users.Add(user);
        db.Goals.AddRange(Enumerable.Range(0, 30).Select(index => Goal.Create(new Goal.CreateGoalParams(
            user.Id, "Goal " + index, 10, "times", Position: index)).Value));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        counter.Reset();
        var today = new DateOnly(2026, 4, 3);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var syncer = Substitute.For<IGoalProgressReadSyncer>();
        syncer.ComputeFreshValuesForGoalsAsync(user.Id, today, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        var tool = new QueryGoalsTool(new GenericRepository<Goal>(db), dates, syncer);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { search, offset = 10, limit = 10 }), user.Id, default);

        var card = (GoalListCard)result.Payload!;
        card.TotalCount.Should().Be(30);
        card.Items.Should().HaveCount(10);
        var ids = card.Items.Select(item => Guid.Parse(item.Id)).ToArray();
        await syncer.Received(1).ComputeFreshValuesForGoalsAsync(user.Id, today,
            Arg.Is<IReadOnlyCollection<Guid>>(values => values.SequenceEqual(ids)), Arg.Any<CancellationToken>());
        counter.Commands.Where(command => command.Sql.Contains("FROM \"Goals\"", StringComparison.Ordinal))
            .Should().OnlyContain(command => command.Rows <= 10);
        counter.Commands.Should().Contain(command => command.Sql.Contains("LIMIT", StringComparison.Ordinal)
            && command.Sql.Contains("OFFSET", StringComparison.Ordinal));
    }
}
