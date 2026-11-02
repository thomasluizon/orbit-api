using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class McpFailureFlagTests
{
    [Theory]
    [InlineData(typeof(AccountTools), "ManageAccount")]
    [InlineData(typeof(ApiKeyTools), "GetApiKeys")]
    [InlineData(typeof(CalendarTools), "ManageCalendarSync")]
    [InlineData(typeof(ChecklistTemplateTools), "CreateChecklistTemplate")]
    [InlineData(typeof(GoalTools), "CreateGoal")]
    [InlineData(typeof(HabitTools), "CreateHabit")]
    [InlineData(typeof(NotificationTools), "MarkAllNotificationsRead")]
    [InlineData(typeof(ProfileTools), "SetName")]
    [InlineData(typeof(SubscriptionTools), "ManageSubscription")]
    [InlineData(typeof(SupportTools), "SendSupportRequest")]
    [InlineData(typeof(TagTools), "CreateTag")]
    public async Task ToolFamily_ExecutorFailure_SetsErrorFlag(Type family, string method)
    {
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(
            new AgentExecuteOperationResponse(new("operation", "operation", AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Denied, PolicyReason: "policy_denied")));
        var bridge = new McpExecutorBridge(executor);
        var target = Activator.CreateInstance(family, family.GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(McpExecutorBridge) ? (object)bridge
                : Substitute.For([parameter.ParameterType], [])).ToArray())!;
        var arguments = family.GetMethod(method)!.GetParameters().Where(parameter => !parameter.HasDefaultValue
                && parameter.ParameterType != typeof(System.Security.Claims.ClaimsPrincipal))
            .ToDictionary(parameter => parameter.Name!, parameter => parameter.ParameterType == typeof(string)
                ? (object?)"test" : Activator.CreateInstance(parameter.ParameterType));
        var result = await McpInputSafetyTests.Invoke(target, method, arguments);
        result.IsError.Should().BeTrue();
        result.AsText().Should().Contain("policy_denied");
    }

    [Fact]
    public async Task ApiKeyTool_WrongPayload_IsError()
    {
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(
            new AgentExecuteOperationResponse(new("get_api_keys", "get_api_keys", AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: new { unexpected = true })));
        var result = await McpInputSafetyTests.Invoke(new ApiKeyTools(new McpExecutorBridge(executor)), nameof(ApiKeyTools.GetApiKeys), new { });
        result.IsError.Should().BeTrue();
        result.AsText().Should().Contain("payload");
    }

    [Fact]
    public async Task CreateHabit_Clarification_IsErrorWithCode()
    {
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(
            new AgentExecuteOperationResponse(new("create_habit", "create_habit", AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded)));
        var result = await McpInputSafetyTests.Invoke(new HabitTools(Substitute.For<IMediator>(), Substitute.For<IUserDateService>(), new McpExecutorBridge(executor)),
            nameof(HabitTools.CreateHabit), new { title = "Read", dueDate = "2026-04-03" });
        result.IsError.Should().BeTrue();
        result.StructuredContent!.Value.GetProperty("code").GetString().Should().Be("more_detail_needed");
    }
}
