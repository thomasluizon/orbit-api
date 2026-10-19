using System.Text.Json;
using FluentAssertions;
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
    [Theory]
    [InlineData("{\"search\":\"AGUA\"}", "Beber água")]
    [InlineData("{\"search\":\"leitura\"}", "Ler 10 minutos")]
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
