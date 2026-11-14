using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
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

    /// <summary>
    /// The person approved the Zinc preview, and it did not run yet. A turn without a conversation sends the same Zinc
    /// creation again, so the store hands the turn the approved row, beside a description that prepares and a skip that
    /// is turned down. The withdrawn turn cancels only the description, the preview that it created.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task AtomicTurn_AWithdrawnTurnCancelsOnlyThePreviewsThatItCreated(string language)
    {
        var parents = ParentAccount(language);
        var earlier = await PrepareEarlierZincAsync(parents);
        _pendingOperationStore.Confirm(_userId, earlier.Id).Should().NotBeNull();
        _ownership.GetDenialReasonAsync("skip_habit", _userId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns("target_not_owned:skip_habit:habit");
        var forwarded = ReplyToTurn(
        [
            new("create_sub_habit", "zinc", JsonSerializer.SerializeToElement(new { parent_habit_id = parents[0].Id, title = "Zinc", frequency_unit = "Day" })),
            new("update_habit", "edit", JsonSerializer.SerializeToElement(new { habit_id = parents[1].Id, description = "Quiet time" })),
            new("skip_habit", "skip", JsonSerializer.SerializeToElement(new { habit_id = UnknownHabitId }))
        ], "Review.");

        var result = await CreateHandler().Handle(new(_userId,
            "Add Zinc under Post Lunch, describe Before Bed as quiet time and skip my walk today."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        forwarded.Should().Contain(item => item.Id == "zinc" && item.Success, "the Zinc creation prepared");
        _dbContext.PendingAgentOperations.Count(row => row.UserId == _userId && row.OperationId == "create_sub_habit")
            .Should().Be(1, "the store handed the turn the approved Zinc row");
        _pendingOperationStore.GetExecution(_userId, earlier.Id).Should().NotBeNull("the turn did not create the Zinc row");
        result.Value.PendingOperations.Should().ContainSingle("the turn withdrew the description").Which.Id.Should().Be(earlier.Id);
        result.Value.AiMessage.Should().Be(EarlierKeptAnswer(language));
    }

    private static AiToolCall NewZinc(Habit[] parents) => new("create_sub_habit", "zinc", JsonSerializer.SerializeToElement(new
    {
        parent_habit_id = parents[0].Id,
        title = "Zinc",
        frequency_unit = "Day",
        description = "With lunch"
    }));

    private ProcessUserChatCommand NewZincPreviewRequest(string message) => new(_userId, message,
        History: [new ChatHistoryMessage("assistant", "Review the Zinc preview.")]);

    /// <summary>
    /// The person asks for a new Zinc preview. The turn prepares it and a description, and a skip is turned down. The
    /// withdrawn turn replaces nothing: the earlier Zinc preview still awaits approval in the store.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task AtomicTurn_AWithdrawnTurnKeepsTheEarlierCreationThatItsNewPreviewWasToReplace(string language)
    {
        var parents = ParentAccount(language);
        var earlier = await PrepareEarlierZincAsync(parents);
        _ownership.GetDenialReasonAsync("skip_habit", _userId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns("target_not_owned:skip_habit:habit");
        ReplyToTurn(
        [
            NewZinc(parents),
            new("update_habit", "edit", JsonSerializer.SerializeToElement(new { habit_id = parents[1].Id, description = "Quiet time" })),
            new("skip_habit", "skip", JsonSerializer.SerializeToElement(new { habit_id = UnknownHabitId }))
        ], "Review.");

        var result = await CreateHandler().Handle(NewZincPreviewRequest(
            "Prepare a new preview for Zinc, describe Before Bed as quiet time and skip my walk today."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, earlier.Id).Should().BeTrue("the withdrawn turn replaces nothing");
        result.Value.PendingOperations.Should().BeNullOrEmpty("the turn withdrew its new Zinc preview and the description");
        result.Value.AiMessage.Should().Be(EarlierKeptAnswer(language));
    }

    /// <summary>The same request in a turn that ends with its answer replaces the earlier Zinc preview with the new one.</summary>
    [Fact]
    public async Task AtomicTurn_ATurnThatEndsWithItsAnswerReplacesTheEarlierCreationWithItsNewPreview()
    {
        var parents = ParentAccount("en");
        var earlier = await PrepareEarlierZincAsync(parents);
        ReplyToTurn([NewZinc(parents)], "Review the new Zinc preview.");

        var result = await CreateHandler().Handle(NewZincPreviewRequest("Prepare a new preview for Zinc."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        _pendingOperationStore.GetExecution(_userId, earlier.Id).Should().BeNull("the new preview replaced it");
        var replacement = result.Value.PendingOperations.Should().ContainSingle().Subject;
        replacement.Id.Should().NotBe(earlier.Id);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, replacement.Id).Should().BeTrue();
    }

    private static string NothingWaitsAnswer(string language) => language == "en"
        ? "I couldn't prepare your whole request, so nothing is waiting for your approval. Please send it again."
        : "Não consegui preparar o seu pedido inteiro, então nada ficou esperando a sua aprovação. Mande de novo, por favor.";

    /// <summary>
    /// The tools of the handler, with a revision tool whose discard the store refuses, as it does when the preview
    /// changed after the revision read it.
    /// </summary>
    private AiToolRegistry RefusedDiscardTools()
    {
        var store = Substitute.For<IPendingAgentOperationStore>();
        store.GetExecution(_userId, Arg.Any<Guid>()).Returns(call => _pendingOperationStore.GetExecution(_userId, call.ArgAt<Guid>(1)));
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(PendingOperationRevisionService)).Returns(new PendingOperationRevisionService(store, _previewer,
            new RevisePendingOperationRequestValidator(), _toolRegistry));
        return new AiToolRegistry(_toolRegistry.GetAll().Select(tool => tool is RevisePendingChangesTool ? new RevisePendingChangesTool(services) : tool));
    }

    private static AiToolCall DescribeBeforeBed(Habit[] parents) => new("update_habit", "edit",
        JsonSerializer.SerializeToElement(new { habit_id = parents[1].Id, description = "Quiet time" }));

    /// <summary>
    /// The turn sends <paramref name="first"/>, then the calls of <paramref name="second"/>, which read the previews that
    /// the first round prepared, and then its answer.
    /// </summary>
    private void ReplyInTwoRounds(IReadOnlyList<AiToolCall> first, Func<AiToolCall[]> second, string answer)
    {
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ConversationContext = new(), ToolCalls = [.. first] }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result.Success(new AiResponse { ConversationContext = new(), ToolCalls = second() }),
                _ => Result.Success(new AiResponse { TextMessage = answer }));
    }

    /// <summary>The id of the description preview that the first round prepared, and a revision that removes its one item.</summary>
    private (Guid Id, AiToolCall Discard) DiscardTheDescription(Habit[] parents)
    {
        var id = _dbContext.PendingAgentOperations.Single(row => row.UserId == _userId && row.OperationId == "update_habit").Id;
        return (id, new("revise_pending_changes", "discard", JsonSerializer.SerializeToElement(new
        {
            pending_operation_id = id,
            items = Array.Empty<object>(),
            remove_item_ids = new[] { parents[1].Id.ToString() }
        })));
    }

    /// <summary>
    /// The turn prepares a description of Before Bed, and the next round removes every item of it beside a skip of a
    /// habit that is not the person's. The store refuses the discard, so the description stays in the pending set:
    /// the withdrawn turn cancels it, and no approval brings it back.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task AtomicTurn_AWithdrawnTurnCancelsItsPreviewWhoseDiscardTheStoreRefused(string language)
    {
        var parents = ParentAccount(language);
        _ownership.GetDenialReasonAsync("skip_habit", _userId, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns("target_not_owned:skip_habit:habit");
        var description = Guid.Empty;
        ReplyInTwoRounds([DescribeBeforeBed(parents)], () =>
        {
            (description, var discard) = DiscardTheDescription(parents);
            return [discard, new("skip_habit", "skip", JsonSerializer.SerializeToElement(new { habit_id = UnknownHabitId }))];
        }, "Review.");

        var result = await CreateHandler(tools: RefusedDiscardTools()).Handle(new(_userId,
            "Describe Before Bed as quiet time, then drop that and skip my walk today."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        description.Should().NotBeEmpty("the first round prepared the description");
        _pendingOperationStore.IsAwaitingConfirmation(_userId, description).Should().BeFalse("the withdrawn turn cancels its own preview");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.AiMessage.Should().Be(NothingWaitsAnswer(language));
    }

    /// <summary>
    /// The same refused discard in a turn that ends with its answer leaves the description in the answer, because it
    /// still waits for the person's approval.
    /// </summary>
    [Fact]
    public async Task AtomicTurn_APreviewWhoseDiscardTheStoreRefusedStaysInTheAnswer()
    {
        var parents = ParentAccount("en");
        var description = Guid.Empty;
        ReplyInTwoRounds([DescribeBeforeBed(parents)], () =>
        {
            (description, var discard) = DiscardTheDescription(parents);
            return [discard];
        }, "Review.");

        var result = await CreateHandler(tools: RefusedDiscardTools()).Handle(new(_userId,
            "Describe Before Bed as quiet time, then drop that."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, description).Should().BeTrue("the store refused the discard");
        result.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(description);
    }
}
