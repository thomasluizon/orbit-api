using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// A turn that the withdrawal ends changes nothing that the person had before it (thomasluizon/orbit-tickets#2129). These
/// turns run the real pending operation store, the real executor and the real revision tool.
/// </summary>
public sealed partial class ChatWriteHoldHandlerTests
{
    private static readonly Guid UnknownHabitId = Guid.Parse("5d0c7a43-2f61-4b8e-9c3a-7e1f0b6d2a95");

    private static string EarlierKeptAnswer(string language) => language == "en"
        ? "I couldn't prepare your whole request, so I left your earlier changes as they were. They are still waiting for your approval. Please send the request again."
        : "Não consegui preparar o seu pedido inteiro, então deixei as alterações anteriores como estavam. Elas continuam esperando a sua aprovação. Mande o pedido de novo, por favor.";

    /// <summary>A first turn prepares the creation of Zinc under Post Lunch, and it awaits approval.</summary>
    private async Task<PendingAgentOperation> PrepareEarlierZincAsync(Habit[] parents)
    {
        ScriptToolCall(JsonSerializer.Serialize(new { parent_habit_id = parents[0].Id, title = "Zinc", frequency_unit = "Day" }),
            "create_sub_habit");
        var first = await CreateHandler().Handle(new(_userId, "Add Zinc under Post Lunch"), default);
        return first.Value.PendingOperations.Should().ContainSingle().Subject;
    }

    private List<AiToolCallResult> ReplyToTurn(IReadOnlyList<AiToolCall> calls, string answer)
    {
        var forwarded = new List<AiToolCallResult>();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ConversationContext = new(), ToolCalls = [.. calls] }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => forwarded.AddRange(results)),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = answer }));
        return forwarded;
    }

    /// <summary>A revision of the earlier preview that removes every item of it.</summary>
    private static AiToolCall RemoveEveryItem(PendingAgentOperation earlier) => new("revise_pending_changes", "discard",
        JsonSerializer.SerializeToElement(new
        {
            pending_operation_id = earlier.Id,
            items = Array.Empty<object>(),
            remove_item_ids = earlier.Items!.Select(item => item.ItemId)
        }));

    /// <summary>
    /// The turn removes every item of the earlier Zinc preview, prepares Magnesium, and gets a description of a habit
    /// that is not the person's turned down. The withdrawn turn closes nothing that the person had: the Zinc preview
    /// still awaits approval in the store, and the answer says that it still waits.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task AtomicTurn_AWithdrawnTurnKeepsTheEarlierPreviewThatItsRevisionEmptied(string language)
    {
        var parents = ParentAccount(language);
        var earlier = await PrepareEarlierZincAsync(parents);
        _ownership.GetDenialReasonAsync("update_habit", _userId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns("target_not_owned:update_habit:habit");
        ReplyToTurn(
        [
            RemoveEveryItem(earlier),
            new("create_sub_habit", "child", JsonSerializer.SerializeToElement(new { parent_habit_id = parents[0].Id, title = "Magnesium", frequency_unit = "Day" })),
            new("update_habit", "edit", JsonSerializer.SerializeToElement(new { habit_id = UnknownHabitId, description = "Quiet time" }))
        ], "Review.");

        var result = await CreateHandler().Handle(new(_userId,
            "Drop Zinc, add Magnesium under Post Lunch and describe the other habit as quiet time."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, earlier.Id).Should().BeTrue("the withdrawn turn discards nothing");
        _pendingOperationStore.GetExecution(_userId, earlier.Id).Should().NotBeNull();
        result.Value.PendingOperations.Should().BeNullOrEmpty("the turn withdrew its own Magnesium preview");
        result.Value.AiMessage.Should().Be(EarlierKeptAnswer(language));
    }

    /// <summary>
    /// The same revision in a turn that ends with its answer discards the earlier preview, and the model learns that
    /// the preview is discarded instead of reading a failure.
    /// </summary>
    [Fact]
    public async Task AtomicTurn_ATurnThatEndsWithItsAnswerDiscardsTheEarlierPreviewThatItsRevisionEmptied()
    {
        var parents = ParentAccount("en");
        var earlier = await PrepareEarlierZincAsync(parents);
        var forwarded = ReplyToTurn([RemoveEveryItem(earlier)], "I dropped the Zinc preview.");

        var result = await CreateHandler().Handle(new(_userId, "Drop the Zinc preview."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        _pendingOperationStore.GetExecution(_userId, earlier.Id).Should().BeNull("the turn ended with its answer");
        forwarded.Should().ContainSingle(item => item.Id == "discard").Which.Success.Should().BeTrue();
        result.Value.AiMessage.Should().Be("I dropped the Zinc preview.");
    }
}
