using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// A failed change list on the <c>ai_change_plan</c> path withdraws only what its own writes prepared
/// (thomasluizon/orbit-tickets#2160). A discard of an earlier preview that a read call of the same response made is not a
/// write of the list, so it commits with the turn. These turns run the real pending operation store, the real executor
/// and the real revision tool.
/// </summary>
public sealed partial class ChatWriteHoldHandlerTests
{
    private static AiResponse ChangeListResponse(ChangeDeclarationShape shape, bool complete, params (string Id, string Kind, object Arguments)[] changes) => new()
    {
        ConversationContext = new(),
        ToolCalls = [.. shape == ChangeDeclarationShape.Wrapper
            ? [new AiToolCall(ChangeDeclarations.WrapperName, "list", JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                [ChangeDeclarations.ChangesArgument] = changes.Select(change => new { id = change.Id, kind = change.Kind, arguments = change.Arguments }),
                [TerminalWriteDirective.CompleteArgument] = complete
            }))]
            : changes.Select(change => new AiToolCall(change.Kind, change.Id, WithComplete(change.Arguments, complete)))]
    };

    private static JsonElement WithComplete(object arguments, bool complete)
    {
        var node = JsonSerializer.SerializeToNode(arguments)!.AsObject();
        node[TerminalWriteDirective.CompleteArgument] = complete;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// The person has the earlier Zinc preview. The first response discards it with a read call and sends a change list
    /// whose one change targets a habit that is not the person's. The repair prepares the description of Before Bed. The
    /// discard is the person's request, so the turn ends with the Zinc preview closed, as a turn with no failed list closes it.
    /// </summary>
    [Theory]
    [InlineData(ChangeDeclarationShape.Kinds, "en")]
    [InlineData(ChangeDeclarationShape.Kinds, "pt-BR")]
    [InlineData(ChangeDeclarationShape.Wrapper, "en")]
    [InlineData(ChangeDeclarationShape.Wrapper, "pt-BR")]
    public async Task ChangeList_AFailedListKeepsTheDiscardOfAnEarlierPreviewThatAReadCallOfTheResponseMade(ChangeDeclarationShape shape, string language)
    {
        var parents = ParentAccount(language);
        var earlier = await PrepareEarlierZincAsync(parents);
        _ownership.GetDenialReasonAsync("update_habit", _userId,
                Arg.Is<JsonElement>(args => args.GetRawText().Contains(UnknownHabitId.ToString())), Arg.Any<CancellationToken>())
            .Returns("target_not_owned:update_habit:habit");
        EnableChangePlanFlag();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = new(),
                ToolCalls = [RemoveEveryItem(earlier), .. ChangeListResponse(shape, true,
                    ("edit", "update_habit", new { habit_id = UnknownHabitId, description = "Quiet time" })).ToolCalls!]
            }));
        var forwarded = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => forwarded.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ChangeListResponse(shape, true,
                    ("edit", "update_habit", new { habit_id = parents[1].Id, description = "Quiet time" }))),
                Result.Success(new AiResponse { TextMessage = "Done." }));

        var result = await CreateHandler(changeShape: shape).Handle(new(_userId, "Drop Zinc and describe Before Bed as quiet time."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        forwarded[0].Should().Contain(item => item.Id == "discard" && item.Success, "the model learns that the discard succeeded");
        forwarded[0].Where(item => item.Id != "discard").Should().OnlyContain(item => !item.Success, "the list failed");
        result.Value.PendingOperations.Should().ContainSingle("the repair prepared the description").Which.Id.Should().NotBe(earlier.Id);
        _pendingOperationStore.GetExecution(_userId, earlier.Id).Should().BeNull("the turn ended with its answer, so the discard committed");
        _pendingOperationStore.IsAwaitingConfirmation(_userId, earlier.Id).Should().BeFalse();
    }
}
