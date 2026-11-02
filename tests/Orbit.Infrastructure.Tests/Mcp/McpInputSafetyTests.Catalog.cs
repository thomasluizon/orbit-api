using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using ModelContextProtocol.Server;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

public class McpInputCatalogTests
{
    private static readonly Type[] IdFamilies = [typeof(HabitTools), typeof(GoalTools), typeof(TagTools),
        typeof(ChecklistTemplateTools), typeof(NotificationTools), typeof(ApiKeyTools)];

    public static IEnumerable<object[]> IdArguments() => IdFamilies.SelectMany(type => type.GetMethods()
        .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .SelectMany(method => method.GetParameters().Where(parameter => parameter.ParameterType == typeof(string)
            && (parameter.Name!.EndsWith("Id", StringComparison.Ordinal) || parameter.Name.EndsWith("Ids", StringComparison.Ordinal)))
            .Select(parameter => new object[] { type, method.Name, parameter.Name! })));

    [Theory]
    [MemberData(nameof(IdArguments))]
    public async Task EveryIdArgument_ReturnsFriendlyErrorBeforeDispatch(Type family, string methodName, string field)
    {
        var mediator = Substitute.For<IMediator>();
        var executor = Substitute.For<IAgentOperationExecutor>();
        var bridge = new McpExecutorBridge(executor);
        var target = family == typeof(HabitTools) ? new HabitTools(mediator, Substitute.For<IUserDateService>(), bridge)
            : Activator.CreateInstance(family, family == typeof(ApiKeyTools) ? [bridge] : new object[] { mediator, bridge })!;
        var arguments = new Dictionary<string, object?>();
        foreach (var parameter in family.GetMethod(methodName)!.GetParameters())
        {
            if (parameter.ParameterType != typeof(string))
            {
                if (!parameter.HasDefaultValue && parameter.ParameterType.IsValueType && parameter.ParameterType != typeof(CancellationToken))
                    arguments[parameter.Name!] = Activator.CreateInstance(parameter.ParameterType);
                continue;
            }
            if (parameter.Name == field) arguments[field] = "not-a-guid";
            else if (!parameter.HasDefaultValue)
                arguments[parameter.Name!] = parameter.Name!.EndsWith("Id", StringComparison.Ordinal) ? Guid.NewGuid().ToString()
                    : parameter.Name.EndsWith("Json", StringComparison.Ordinal) ? "[]" : "2026-01-01";
        }
        var result = await McpInputSafetyTests.Invoke(target, methodName, arguments);
        result.IsError.Should().BeTrue();
        result.AsText().Should().Contain("'not-a-guid' is not a valid GUID.");
        executor.ReceivedCalls().Should().BeEmpty();
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void ConfirmationDescriptions_AgreeWithCapabilityRequirements()
    {
        var catalog = new AgentCatalogService([]);
        foreach (var type in typeof(HabitTools).Assembly.GetTypes())
            foreach (var method in type.GetMethods().Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null))
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                foreach (var parameter in method.GetParameters().Where(parameter => parameter.GetCustomAttribute<DescriptionAttribute>()?.Description.Contains("required:", StringComparison.OrdinalIgnoreCase) == true))
                    catalog.GetCapabilityByMcpTool(name)!.ConfirmationRequirement.Should().NotBe(AgentConfirmationRequirement.None,
                        $"{name}.{parameter.Name} claims confirmation is required");
            }
    }

    [Theory]
    [InlineData("99")]
    [InlineData("Paused")]
    public async Task GoalStatus_UndefinedValues_AreErrors(string status)
    {
        var mediator = Substitute.For<IMediator>();
        var result = await McpInputSafetyTests.Invoke(new GoalTools(mediator, new McpExecutorBridge(Substitute.For<IAgentOperationExecutor>())),
            nameof(GoalTools.ListGoals), new { status });
        result.IsError.Should().BeTrue();
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("5")]
    [InlineData("{bad")]
    [InlineData("null")]
    public async Task BulkFilter_RequiresAnObjectBeforeExecutor(string filter)
    {
        var executor = Substitute.For<IAgentOperationExecutor>();
        var target = new HabitTools(Substitute.For<IMediator>(), Substitute.For<IUserDateService>(), new McpExecutorBridge(executor));
        var result = await McpInputSafetyTests.Invoke(target, nameof(HabitTools.BulkDeleteHabits), new { filterJson = filter });
        result.IsError.Should().BeTrue();
        executor.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(AgentOperationStatus.Succeeded, false)]
    [InlineData(AgentOperationStatus.PendingConfirmation, false)]
    [InlineData(AgentOperationStatus.Denied, true)]
    [InlineData(AgentOperationStatus.Failed, true)]
    [InlineData(AgentOperationStatus.UnsupportedByPolicy, true)]
    public async Task OperationGateway_PreservesBodyAndFlagsStatus(AgentOperationStatus status, bool error)
    {
        var executor = Substitute.For<IAgentOperationExecutor>();
        var response = new AgentExecuteOperationResponse(new("create_tag", "create_tag", AgentRiskClass.Low, AgentConfirmationRequirement.None, status));
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(response);
        var tools = new AgentTools(Substitute.For<IAgentCatalogService>(), executor,
            Substitute.For<IPendingAgentOperationStore>(), Substitute.For<IAgentStepUpService>());
        var result = await McpInputSafetyTests.Invoke(tools, nameof(AgentTools.ExecuteAgentOperation), new { operationId = "create_tag" });
        result.IsError.Should().Be(error);
        result.AsText().Should().Be(JsonSerializer.Serialize(response, ModelContextProtocol.McpJsonUtilities.DefaultOptions));
        var legacy = await McpInputSafetyTests.Invoke(new UnwrappedOperation(response), nameof(UnwrappedOperation.Get), new { });
        result.AsText().Should().Be(legacy.AsText());
    }

    public sealed class UnwrappedOperation(AgentExecuteOperationResponse response)
    {
        public AgentExecuteOperationResponse Get() => response;
    }

    [Fact]
    public void InvalidIdentity_UsesTheSharedUnauthorizedFailure()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "not-a-guid")]));
        var shared = () => Orbit.Api.Extensions.HttpContextExtensions.GetUserId(user);
        var tools = () => McpToolHelpers.GetUserId(user);
        shared.Should().Throw<UnauthorizedAccessException>();
        tools.Should().Throw<UnauthorizedAccessException>();
    }
}
