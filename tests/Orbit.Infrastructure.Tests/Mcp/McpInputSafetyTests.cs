using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class McpInputSafetyTests
{
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly HabitTools _tools;

    public McpInputSafetyTests()
    {
        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(
            new AgentExecuteOperationResponse(new AgentOperationResult("reorder_habits", "reorder_habits",
                AgentRiskClass.Low, AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded)));
        _tools = new HabitTools(_mediator, Substitute.For<IUserDateService>(), new McpExecutorBridge(_executor));
    }

    [Fact]
    public async Task GetHabit_InvalidId_ReturnsFriendlyErrorThroughSdk()
    {
        var result = await Invoke(_tools, nameof(HabitTools.GetHabit), new { habitId = "not-a-guid" });
        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("Invalid habitId: 'not-a-guid' is not a valid GUID.");
        _mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ReorderHabits_JsonNull_ReturnsErrorWithoutExecutorCall()
    {
        var result = await Invoke(_tools, nameof(HabitTools.ReorderHabits), new { positionsJson = "null" });
        result.IsError.Should().BeTrue();
        _executor.ReceivedCalls().Should().BeEmpty();
    }

    internal static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(content => content.Text));

    internal static async Task<CallToolResult> Invoke(object target, string methodName, object arguments)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var tool = McpServerTool.Create(target.GetType().GetMethod(methodName)!, target);
        var context = new RequestContext<CallToolRequestParams>(Substitute.For<McpServer>(),
            new JsonRpcRequest { Id = new RequestId(1), Method = "tools/call" },
            new CallToolRequestParams { Name = tool.ProtocolTool.Name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments)) })
        { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test")), Services = services };
        return await tool.InvokeAsync(context);
    }
}
