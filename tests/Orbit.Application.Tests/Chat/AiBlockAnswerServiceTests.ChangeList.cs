using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

/// <summary>
/// The pickList answer on the <c>ai_change_plan</c> path (thomasluizon/orbit-tickets#2160): the chosen items compile
/// with the change list compiler, with no model call, through the same executor call as the old path.
/// </summary>
public partial class AiBlockAnswerServiceTests
{
    private sealed class BulkKindTool(string name) : IAiTool
    {
        public string Name => name;
        public string Description => name;
        public object GetParameterSchema() => new { type = "object", properties = new { } };
        public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) => Task.FromResult(new ToolResult(true));
    }

    private static readonly AiToolRegistry PickListKinds = new(
        [new BulkKindTool("bulk_create_habits"), new BulkKindTool("bulk_log_habits"), new BulkKindTool("bulk_skip_habits")]);

    [Theory]
    [InlineData("create_habits")]
    [InlineData("log_habits")]
    [InlineData("skip_habits")]
    public async Task PickListAnswer_OnTheChangePlanPathGivesTheSamePreviewThroughTheSameExecutorCall(string action)
    {
        var items = Enumerable.Range(0, 4).Select(i => new AiBlockItem(i.ToString(), "Habit " + i,
            action == "create_habits" ? JsonSerializer.SerializeToElement(new { title = "Habit " + i })
                : JsonSerializer.SerializeToElement(new { habit_id = Guid.NewGuid(), date = "2026-04-03" }))).ToArray();
        var requests = new List<AgentExecuteOperationRequest>();
        var responses = new List<PendingAgentOperation>();
        PendingAgentOperation? current = null;
        _executor.ExecuteAsync(Arg.Do<AgentExecuteOperationRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(_ => new AgentExecuteOperationResponse(new("bulk_" + action, "bulk_" + action, AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), current));
        foreach (var changePlan in new[] { false, true })
        {
            var block = IssuedAiBlock.Create(UserId, new("pickList", "Choose", [], items, true, Action: action));
            _store.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
            _store.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
            var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", "Habits", "Preview",
                AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc,
                Items: [new("1", null, "Habit 1", [], "fingerprint1"), new("3", null, "Habit 3", [], "fingerprint3")]);
            current = pending;
            _featureFlags.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
                .Returns(changePlan ? [FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiChangePlan] : [FeatureFlagKeys.AiBlockFirst]);
            var service = new AiBlockAnswerService(_store, _work, _quota, _executor, _users, _analytics, _featureFlags,
                Substitute.For<IPendingAgentOperationStore>(), _dates, clarifications: _clarifications, registry: PickListKinds);

            var result = await service.PrepareAsync(Request(block.Id, ["3", "1"]) with
            { ClientContext = new(SupportsParts: true, ConversationId: Guid.NewGuid()) }, default);

            result.IsSuccess.Should().BeTrue(result.Error);
            responses.Add(result.Value.Response!.PendingOperations.Should().ContainSingle().Subject);
            responses[^1].Should().Be(pending);
        }

        requests.Should().HaveCount(2);
        requests[1].OperationId.Should().Be("bulk_" + action).And.Be(requests[0].OperationId);
        requests[1].Arguments.GetRawText().Should().Be(requests[0].Arguments.GetRawText());
        requests[1].PreviewOnly.Should().BeTrue();
        requests[1].ClientContext.Should().Be(requests[0].ClientContext, "the call stays as it is until #2155 passes the context");
        await _quota.DidNotReceiveWithAnyArgs().TryConsumeAiMessage(default, default!, default);
    }

    [Fact]
    public async Task PickListAnswer_OnTheChangePlanPathWithdrawsAPreviewThatMissesAChosenItem()
    {
        var items = Enumerable.Range(0, 2).Select(i => new AiBlockItem(i.ToString(), "Habit " + i,
            JsonSerializer.SerializeToElement(new { habit_id = Guid.NewGuid(), date = "2026-04-03" }))).ToArray();
        var block = IssuedAiBlock.Create(UserId, new("pickList", "Choose", [], items, true, Action: "log_habits"));
        _store.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _store.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        var pending = new PendingAgentOperation(Guid.NewGuid(), "habits", "Habits", "Preview",
            AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, block.ExpiresAtUtc,
            Items: [new("0", null, "Habit 0", [], "fingerprint0")]);
        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new("bulk_log_habits", "bulk_log_habits", AgentRiskClass.Low,
                AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending));
        _featureFlags.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AiBlockFirst, FeatureFlagKeys.AiChangePlan]);
        var service = new AiBlockAnswerService(_store, _work, _quota, _executor, _users, _analytics, _featureFlags,
            Substitute.For<IPendingAgentOperationStore>(), _dates, clarifications: _clarifications, registry: PickListKinds);

        var result = await service.PrepareAsync(Request(block.Id, ["0", "1"]) with { ClientContext = new(SupportsParts: true) }, default);

        result.ErrorCode.Should().Be(ErrorCodes.AiBlockPreviewUnavailable);
    }
}
