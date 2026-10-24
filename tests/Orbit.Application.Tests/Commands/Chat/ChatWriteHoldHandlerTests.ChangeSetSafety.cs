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
}
