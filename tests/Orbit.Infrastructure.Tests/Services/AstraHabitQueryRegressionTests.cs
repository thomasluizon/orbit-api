using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class AstraHabitQueryRegressionTests
{
    [Fact]
    public async Task RoundThree_FilteredMetrics_MaterializesOnlyMatchingHistory()
    {
        var materialized = new LogMaterializationCounter();
        using var factory = new SqliteOrbitDbContextFactory(materialized);
        var db = factory.Context;
        var user = User.Create("Test", "query@example.com").Value;
        var today = new DateOnly(2026, 10, 19);
        var water = Habit.Create(new HabitCreateParams(user.Id, "Beber água", FrequencyUnit.Day, 1, today)).Value;
        var reading = Habit.Create(new HabitCreateParams(user.Id, "Ler 10 minutos", FrequencyUnit.Day, 1, today)).Value;
        water.Log(today.AddDays(-1));
        reading.Log(today.AddDays(-1));
        db.Users.Add(user);
        db.Habits.AddRange(water, reading);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var tool = new QueryHabitsTool(new GenericRepository<Habit>(db), new GenericRepository<User>(db), dates,
            Substitute.For<IHabitScheduleLogReader>());

        var result = await tool.ExecuteAsync(JsonDocument.Parse("""{"search":"AGUA","include_metrics":true}""").RootElement, user.Id, default);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Beber água").And.NotContain("Ler 10 minutos");
        materialized.HabitIds.Should().OnlyContain(id => id == water.Id);
        materialized.HabitIds.Should().Contain(water.Id);
    }

    private sealed class LogMaterializationCounter : IMaterializationInterceptor
    {
        public List<Guid> HabitIds { get; } = [];
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is HabitLog log)
                HabitIds.Add(log.HabitId);
            return entity;
        }
    }

    [Theory]
    [InlineData("{\"search\":\"AGUA\"}", "Beber água")]
    [InlineData("{\"search\":\"LER\"}", "Ler 10 minutos")]
    [InlineData("{\"tag\":\"saude\"}", "Beber água")]
    public async Task QueryHabits_AccountShape_SearchExecutesOnRelationalProvider(string arguments, string expected)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Test", "test@example.com").Value;
        var today = new DateOnly(2026, 10, 19);
        var water = Habit.Create(new HabitCreateParams(user.Id, "Beber água", FrequencyUnit.Day, 1, today)).Value;
        var reading = Habit.Create(new HabitCreateParams(user.Id, "Ler 10 minutos", FrequencyUnit.Day, 1, today)).Value;
        var tag = Tag.Create(user.Id, "Saúde", "#123456").Value;
        water.AddTag(tag);
        db.Users.Add(user);
        db.Tags.Add(tag);
        db.Habits.AddRange(water, reading);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var tool = new QueryHabitsTool(new GenericRepository<Habit>(db), new GenericRepository<User>(db),
            dates, Substitute.For<IHabitScheduleLogReader>());

        var result = await tool.ExecuteAsync(JsonDocument.Parse(arguments).RootElement, user.Id, default);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain(expected);
    }
}
