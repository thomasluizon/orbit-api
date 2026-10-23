using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.Reflection;
using Xunit.Abstractions;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class HabitRankingToolsTests(ITestOutputHelper output)
{
    [Fact]
    public void Read_IsRegisteredWithTheResolvedQuerySchema()
    {
        var builder = WebApplication.CreateBuilder();
        typeof(Orbit.Api.Extensions.ServiceCollectionExtensions).GetMethod("AddMcpToolServer",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [builder]);
        using var services = builder.Services.BuildServiceProvider();
        var registered = services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        var tool = registered.Single(t => t.ProtocolTool.Name == "get_habit_ranking");
        var actual = JsonSerializer.Serialize(tool.ProtocolTool);
        output.WriteLine(actual);
        var schema = JsonDocument.Parse(actual).RootElement;
        schema.GetProperty("name").GetString().Should().Be("get_habit_ranking");
        var query = schema.GetProperty("inputSchema").GetProperty("properties").GetProperty("query");
        query.GetProperty("type").GetString().Should().Be("object");
        query.GetProperty("properties").EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            ["dateFrom", "dateToExclusive", "window", "months", "metric", "habitIds", "pageSize", "queryToken", "cursor", "refresh"]);
    }

    [Fact]
    public async Task Read_UsesTheAuthenticatedOwner_AndPreservesTheBoundQuery()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())]));
        var from = new DateOnly(2024, 1, 1);
        var response = new HabitRankingResponse("query", "signed-query", "revision",
            new HabitRankingResolvedQuery(from, from.AddDays(1), "UTC", "completion_log_records", "Positive log records",
                new HabitRankingScope(), [], 20), [], 0, 0, true, null);
        mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(response));
        var tools = new HabitTools(mediator, Substitute.For<IUserDateService>(),
            new McpExecutorBridge(Substitute.For<IAgentOperationExecutor>()));

        var result = await tools.GetHabitRanking(user, new HabitRankingReadRequest(QueryToken: "signed-query", Refresh: true));

        await mediator.Received(1).Send(Arg.Is<GetHabitRankingQuery>(q => q.UserId == userId
            && q.QueryToken == "signed-query" && q.Refresh && q.DateFrom == null), Arg.Any<CancellationToken>());
        var wire = JsonDocument.Parse(result).RootElement;
        wire.GetProperty("queryId").GetString().Should().Be(response.QueryId);
        wire.GetProperty("queryToken").GetString().Should().Be(response.QueryToken);
        wire.GetProperty("totalCount").GetInt32().Should().Be(0);
    }
}
