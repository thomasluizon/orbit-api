using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Habits.Queries;
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

    [Fact]
    public void MetricsCardProduced_IsNotShareable()
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();
        var metricsCard = new MetricsCard("week", 50, 2, 4, 2, 1, 3, true, "progress");

        ProcessUserChatCommandHandler.IsShareableFaqTurn(results, new ResponseCards(null, MetricsCard: metricsCard)).Should().BeFalse();
    }

    [Fact]
    public void AnyOtherCardProduced_IsNotShareable()
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();
        var cards = new ResponseCards("Done", RecordLists: [new RecordListCard("tags", 0, [])]);

        ProcessUserChatCommandHandler.IsShareableFaqTurn(results, cards).Should().BeFalse();
    }

    [Fact]
    public void LastSuccessfulPayload_SkipsFailedLaterCall()
    {
        var results = new ProcessUserChatCommandHandler.ToolExecutionAccumulator();
        var first = new RetrospectiveResponse("week",
            new RetrospectiveMetrics(0, 0, 0, 0, 7, 0, 0, 0, new int[7], [], []),
            new RetrospectiveNarrative("first", "", "", ""), false);
        var second = first with { Period = "month" };
        results.Add("get_retrospective", null,
            Operation("get_retrospective", AgentOperationStatus.Succeeded) with { Payload = first }, null, null);
        results.Add("get_retrospective", null,
            Operation("get_retrospective", AgentOperationStatus.Succeeded) with { Payload = second }, null, null);
        results.Add("get_retrospective", null,
            Operation("get_retrospective", AgentOperationStatus.Failed) with { Payload = first }, null, null);

        results.LastSuccessfulPayload<RetrospectiveResponse>("get_retrospective").Should().Be(second);
    }
}
