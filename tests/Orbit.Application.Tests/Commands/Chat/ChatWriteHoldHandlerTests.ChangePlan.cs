using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The message routes on the <c>ai_change_plan</c> path (thomasluizon/orbit-tickets#2159): a message that matches the
/// placement, pending-parent or explicit-heading pattern reaches the model, and the server writes no preview for it.
/// </summary>
public partial class ChatWriteHoldHandlerTests
{
    private const string ModelReply = "Tell me more.";

    private void EnableChangePlanFlag() => _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
        .Returns([FeatureFlagKeys.AiChangePlan]);

    private void ReplyWithText()
    {
        _aiIntentService.ClearReceivedCalls();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = ModelReply }));
    }

    [Theory]
    [InlineData("en", "make it a checklist item instead")]
    [InlineData("pt-BR", "faça dele um item de lista")]
    public async Task ChangePlan_PlacementSentenceReachesTheModel(string language, string message)
    {
        var parents = ParentAccount(language);
        ScriptToolCall(JsonSerializer.Serialize(new { parent_habit_id = parents[0].Id, title = "Stretch", frequency_unit = "Day" }),
            "create_sub_habit");
        var handler = CreateHandler();
        var initial = await handler.Handle(new(_userId, "Add Stretch to Post Lunch", ClientContext: new(SupportsPendingOperationPlacement: true)), default);
        var pending = initial.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetRawText();
        EnableChangePlanFlag();
        ReplyWithText();

        var reply = await handler.Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new AgentClientContext(SupportsPendingOperationPlacement: true)), default);

        reply.IsSuccess.Should().BeTrue(reply.Error);
        reply.Value.AiMessage.Should().Be(ModelReply);
        reply.Value.PendingOperations.Should().BeNullOrEmpty();
        _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetRawText().Should().Be(before);
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "make them sub-habits")]
    [InlineData("pt-BR", "corrige")]
    public async Task ChangePlan_PendingParentCorrectionReachesTheModel(string language, string correction)
    {
        ParentAccount(language);
        ScriptParentBatch();
        var handler = CreateHandler();
        var first = await handler.Handle(new(_userId, "Create two habits, take vitamin D weekly and take rovustatina daily."), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        EnableChangePlanFlag();
        ReplyWithText();

        var reply = await handler.Handle(new(_userId, correction,
            History: [new("user", OwnerParentRequest), new("user", "will they be sub-habits of Before Bed and Post Lunch?")]), default);

        reply.IsSuccess.Should().BeTrue(reply.Error);
        reply.Value.AiMessage.Should().Be(ModelReply);
        reply.Value.PendingOperations.Should().BeNullOrEmpty();
        _pendingOperationStore.GetExecution(_userId, pending.Id).Should().NotBeNull("no server route replaced the preview");
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "Post lunch: take vitamin D (once a week)", true)]
    [InlineData("pt-BR", "Post lunch: tomar vitamina D (uma vez por semana)", true)]
    [InlineData("en", "Post lunch: take vitamin D (once a week)", false)]
    [InlineData("pt-BR", "Post lunch: tomar vitamina D (uma vez por semana)", false)]
    public async Task ExplicitHeadingMessage_GetsAServerPreviewOnlyOffTheFlag(string language, string message, bool changePlan)
    {
        ParentAccount(language);
        if (changePlan)
            EnableChangePlanFlag();
        ReplyWithText();

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new AgentClientContext(SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        if (changePlan)
        {
            result.Value.PendingOperations.Should().BeNullOrEmpty();
            result.Value.AiMessage.Should().Be(ModelReply);
        }
        else
        {
            result.Value.PendingOperations.Should().ContainSingle();
        }
    }
}
