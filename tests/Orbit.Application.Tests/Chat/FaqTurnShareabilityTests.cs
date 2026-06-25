using FluentAssertions;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class FaqTurnShareabilityTests
{
    private static AgentOperationResult Operation(string name, AgentOperationStatus status) =>
        new(name, name, AgentRiskClass.Low, AgentConfirmationRequirement.None, status);

    [Fact]
    public void NoToolsRan_IsShareable()
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();

        ProcessUserChatCommandHandler.IsShareableFaqTurn(results).Should().BeTrue();
    }

    [Fact]
    public void OnlyDescribeFeatureSucceeded_IsShareable()
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();
        results.Add("describe_feature", null, Operation("describe_feature", AgentOperationStatus.Succeeded), null, null);

        ProcessUserChatCommandHandler.IsShareableFaqTurn(results).Should().BeTrue();
    }

    [Fact]
    public void UserSpecificToolAlsoRan_IsNotShareable()
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();
        results.Add("describe_feature", null, Operation("describe_feature", AgentOperationStatus.Succeeded), null, null);
        results.Add("get_streak_info", null, Operation("get_streak_info", AgentOperationStatus.Succeeded), null, null);

        ProcessUserChatCommandHandler.IsShareableFaqTurn(results).Should().BeFalse();
    }

    [Fact]
    public void DescribeFeatureFailed_IsNotShareable()
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();
        results.Add("describe_feature", null, Operation("describe_feature", AgentOperationStatus.Failed), null, null);

        ProcessUserChatCommandHandler.IsShareableFaqTurn(results).Should().BeFalse();
    }

    [Fact]
    public void PendingConfirmationRaised_IsNotShareable()
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();
        var pending = new PendingAgentOperation(
            Guid.NewGuid(), "manage_calendar_sync", "Manage Calendar Sync", "summary",
            AgentRiskClass.Destructive, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10));
        results.Add("manage_calendar_sync", null, null, null, pending);

        ProcessUserChatCommandHandler.IsShareableFaqTurn(results).Should().BeFalse();
    }
}
