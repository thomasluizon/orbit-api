using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Support.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompoundPreview_ExternalEffectKeepsIndividualApprovalWhileDatabaseMembersCombine(bool twoDatabaseMembers)
    {
        var parents = ParentAccount("en");
        ScriptToolCall("{}", "update_habit");
        var calls = new List<AiToolCall>
        {
            new("create_sub_habit", "child", JsonSerializer.SerializeToElement(new { parent_habit_id = parents[0].Id, title = "Magnesium", frequency_unit = "Day" })),
            new("send_support_request", "support", JsonSerializer.SerializeToElement(new { name = "Alex", email = "alex@test.com", subject = "Help", message = "Please help" }))
        };
        if (twoDatabaseMembers)
            calls.Add(new("update_habit", "edit", JsonSerializer.SerializeToElement(new { habit_id = parents[1].Id, description = "Quiet time" })));
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls, ConversationContext = new() }));

        var result = await CreateHandler().Handle(new(_userId, "Add Magnesium under Post Lunch and send support a request"), default);

        result.IsSuccess.Should().BeTrue();
        var previews = result.Value.PendingOperations.Should().HaveCount(2).And.Subject;
        var stored = previews.Select(preview => _pendingOperationStore.GetExecution(_userId, preview.Id)!).ToArray();
        stored.Should().ContainSingle(member => member.OperationId == "send_support_request");
        if (twoDatabaseMembers)
        {
            var combined = stored.Single(member => member.OperationId == AccountChangeSet.OperationName);
            AccountChangeSet.Read(combined.Arguments)!.Select(member => member.OperationId)
                .Should().BeEquivalentTo(["create_sub_habit", "update_habit"]);
        }
        else stored.Should().ContainSingle(member => member.OperationId == "create_sub_habit");
        await _mediator.DidNotReceive().Send(Arg.Any<SendSupportCommand>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A turn reads a preview from an earlier turn and prepares two writes. Only the two writes of the turn combine
    /// into one approval, so the earlier card stays its own card and the person can still approve it alone.
    /// </summary>
    [Fact]
    public async Task CompoundPreview_KeepsAnEarlierPreviewThatTheTurnOnlyReadOutOfTheCombinedApproval()
    {
        var parents = ParentAccount("en");
        ScriptToolCall(JsonSerializer.Serialize(new { parent_habit_id = parents[0].Id, title = "Zinc", frequency_unit = "Day" }), "create_sub_habit");
        var first = await CreateHandler().Handle(new(_userId, "Add Zinc under Post Lunch"), default);
        var earlier = first.Value.PendingOperations.Should().ContainSingle().Which;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = new(),
                ToolCalls =
                [
                    new("revise_pending_changes", "inspect", JsonSerializer.SerializeToElement(new
                    {
                        pending_operation_id = earlier.Id, items = Array.Empty<object>(), answer_question = true
                    })),
                    new("create_sub_habit", "child", JsonSerializer.SerializeToElement(new { parent_habit_id = parents[0].Id, title = "Magnesium", frequency_unit = "Day" })),
                    new("update_habit", "edit", JsonSerializer.SerializeToElement(new { habit_id = parents[1].Id, description = "Quiet time" }))
                ]
            }));

        var result = await CreateHandler().Handle(new(_userId,
            "Is Zinc fine there? Also add Magnesium under Post Lunch and describe the other habit as quiet time."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().Contain(preview => preview.Id == earlier.Id);
        var combined = result.Value.PendingOperations!.Select(preview => _pendingOperationStore.GetExecution(_userId, preview.Id)!)
            .Should().ContainSingle(member => member.OperationId == AccountChangeSet.OperationName).Subject;
        AccountChangeSet.Read(combined.Arguments)!.Select(member => member.OperationId)
            .Should().BeEquivalentTo(["create_sub_habit", "update_habit"]);
        _pendingOperationStore.Confirm(_userId, earlier.Id).Should().NotBeNull("no change set holds the earlier card");
    }
}
