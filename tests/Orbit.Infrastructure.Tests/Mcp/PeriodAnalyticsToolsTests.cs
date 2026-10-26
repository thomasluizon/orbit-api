using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Mcp;

public sealed class PeriodAnalyticsToolsTests
{
    [Fact]
    public void PeriodReadIsRegisteredWithExactDatesAndMetric()
    {
        var builder = WebApplication.CreateBuilder();
        typeof(Orbit.Api.Extensions.ServiceCollectionExtensions).GetMethod("AddMcpToolServer",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [builder]);
        using var services = builder.Services.BuildServiceProvider();
        var tools = services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        var tool = tools.Single(tool => tool.ProtocolTool.Name == "get_period_analytics");
        var schema = JsonSerializer.SerializeToElement(tool.ProtocolTool);
        var properties = schema.GetProperty("inputSchema").GetProperty("properties").GetProperty("query")
            .GetProperty("properties");
        properties.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["dateFrom", "dateTo", "metric", "habitId", "baselineFrom", "baselineTo", "search", "tag", "isBadHabit"]);
        schema.GetProperty("description").GetString().Should().Contain("completedHabits").And.Contain("positiveLogRecords");
    }

    [Fact]
    public async Task PeriodReadUsesAuthenticatedOwnerAndReturnsProducedHabitCounts()
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        var from = new DateOnly(2026, 4, 1);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Read", FrequencyUnit.Day, 1,
            DueDate: from)).Value;
        habit.Log(from, advanceDueDate: false);
        habit.Log(from.AddDays(1), advanceDueDate: false);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        habits.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        var handler = new GetPeriodAnalyticsQueryHandler(habits, users);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetPeriodAnalyticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<GetPeriodAnalyticsQuery>(), call.Arg<CancellationToken>()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())]));
        var tools = new HabitTools(mediator, Substitute.For<IUserDateService>(),
            new McpExecutorBridge(Substitute.For<IAgentOperationExecutor>()));

        var result = await tools.GetPeriodAnalytics(principal, new(from, from.AddDays(6), "completedHabits",
            BaselineFrom: from.AddDays(-7), BaselineTo: from.AddDays(-1), Search: "Read"));

        await mediator.Received(1).Send(Arg.Is<GetPeriodAnalyticsQuery>(query => query.UserId == user.Id
            && query.Metric == "completedHabits" && query.DateFrom == from && query.Search == "Read"
            && query.BaselineTo == from.AddDays(-1)), Arg.Any<CancellationToken>());
        var wire = JsonDocument.Parse(result).RootElement;
        wire.GetProperty("total").GetProperty("value").GetInt32().Should().Be(1);
        wire.GetProperty("total").GetProperty("unit").GetString().Should().Be("habits");
        wire.GetProperty("total").GetProperty("metrics").GetProperty("positiveLogRecords").GetInt32().Should().Be(2);
        wire.GetProperty("comparison").GetProperty("current").GetProperty("completedHabits").GetInt32().Should().Be(1);
        wire.GetProperty("comparison").GetProperty("baseline").GetProperty("completedHabits").GetInt32().Should().Be(0);
    }
}
