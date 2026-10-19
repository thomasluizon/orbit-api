using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using MediatR;
using ModelContextProtocol.Server;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Mcp;

public sealed class SuppressedToolSchemaTests
{
    [Theory]
    [InlineData(typeof(ApiKeyTools), nameof(ApiKeyTools.ManageApiKeys))]
    [InlineData(typeof(GoalTools), nameof(GoalTools.CreateGoal))]
    [InlineData(typeof(GoalTools), nameof(GoalTools.UpdateGoal))]
    [InlineData(typeof(HabitTools), nameof(HabitTools.ListHabits))]
    [InlineData(typeof(HabitTools), nameof(HabitTools.CreateHabit))]
    [InlineData(typeof(HabitTools), nameof(HabitTools.UpdateHabit))]
    [InlineData(typeof(HabitTools), nameof(HabitTools.CreateSubHabit))]
    public void InstalledSdk_PreservesFlatDescribedParameters(Type type, string methodName)
    {
        var mediator = Substitute.For<IMediator>();
        var bridge = new McpExecutorBridge(Substitute.For<IAgentOperationExecutor>());
        object target = type == typeof(ApiKeyTools)
            ? new ApiKeyTools(bridge)
            : type == typeof(GoalTools)
                ? new GoalTools(mediator, bridge)
                : new HabitTools(mediator, Substitute.For<IUserDateService>(), bridge);
        var method = type.GetMethod(methodName) ?? throw new InvalidOperationException(methodName);
        var tool = McpServerTool.Create(method, target);
        var schema = tool.ProtocolTool.InputSchema;
        schema.GetProperty("type").GetString().Should().Be("object");

        var describedParameters = method.GetParameters()
            .Select(parameter => (parameter.Name, parameter.HasDefaultValue, Description: parameter.GetCustomAttribute<DescriptionAttribute>()))
            .Where(parameter => parameter.Description is not null)
            .ToList();
        var properties = schema.GetProperty("properties");
        describedParameters.Should().NotBeEmpty();
        properties.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(describedParameters.Select(parameter => parameter.Name));
        schema.GetProperty("required").EnumerateArray().Select(property => property.GetString())
            .Should().BeEquivalentTo(describedParameters.Where(parameter => !parameter.HasDefaultValue).Select(parameter => parameter.Name));
        foreach (var parameter in describedParameters)
            properties.GetProperty(parameter.Name!).GetProperty("description").GetString()
                .Should().Be(parameter.Description!.Description);
    }
}
