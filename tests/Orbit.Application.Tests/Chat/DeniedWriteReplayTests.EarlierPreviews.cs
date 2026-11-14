using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// A preview from an earlier turn awaits approval when a turn with a denied write starts (thomasluizon/orbit-tickets#2129).
/// The turn reads it, revises it in place or covers it with a bulk write, and a withdrawn turn leaves it as it was.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private const string ReviseTool = "revise_pending_changes";
    private static readonly Guid EarlierPreviewId = Guid.Parse("7b3e9f21-4c8a-4d6e-b1f0-2a9c5d7e3f84");
    private const string EarlierTitle = "Water - 750ml";
    private const string RevisedTitle = "Water - 1L";

    private static JsonElement EarlierPreviewArguments(Habit[] children, string title) =>
        JsonSerializer.SerializeToElement(new { habit_id = children[2].Id, title });

    private static PendingAgentOperationRevision EarlierRevision(Habit[] children)
    {
        var arguments = EarlierPreviewArguments(children, EarlierTitle).GetRawText();
        return new(arguments, AgentOperationFingerprint.Compute("update_habit", arguments), "earlier-preview", null, false, 1);
    }

    private static string EarlierKeptFor(string language) => language == "en"
        ? "I couldn't prepare your whole request, so I left your earlier changes as they were. They are still waiting for your approval. Please send the request again."
        : "Não consegui preparar o seu pedido inteiro, então deixei as alterações anteriores como estavam. Elas continuam esperando a sua aprovação. Mande o pedido de novo, por favor.";

    private static AiToolCall InspectEarlierPreview() => new(ReviseTool, "inspect", JsonSerializer.SerializeToElement(new
    {
        pending_operation_id = EarlierPreviewId,
        items = Array.Empty<object>(),
        answer_question = true
    }));

    private static AiToolCall ReviseEarlierPreview() => new(ReviseTool, "revise", JsonSerializer.SerializeToElement(new
    {
        pending_operation_id = EarlierPreviewId,
        items = new[] { new { item_id = "0", edits_json = JsonSerializer.Serialize(new { title = RevisedTitle }) } },
        answer_question = false
    }));

    /// <summary>
    /// A preview from an earlier turn renames Water 3 and awaits approval when the turn starts. A revision in this turn
    /// changes its title in place and keeps its id; an inspection returns it as it is.
    /// </summary>
    private void ArrangeEarlierPreview(Habit[] children, bool revised)
    {
        PendingAgentOperationExecution Stored(string title, string preview) => new(EarlierPreviewId, "habits.update", "update_habit",
            EarlierPreviewArguments(children, title), AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation, preview);
        var earlier = Stored(EarlierTitle, "earlier-preview");
        _pendingOperationStore.GetAwaitingAsync(UserId, Arg.Any<CancellationToken>()).Returns([earlier]);
        _pendingOperationStore.GetRevision(UserId, EarlierPreviewId).Returns(EarlierRevision(children));
        _pendingOperationStore.GetExecution(UserId, EarlierPreviewId)
            .Returns(revised ? Stored(RevisedTitle, "revised-preview") : earlier);
        var preview = new PendingAgentOperation(EarlierPreviewId, "habits.update", "update_habit", "update_habit", AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10), ChangeTargetCount: 1,
            Items: [new PendingOperationItem("0", children[2].Id, children[2].Title, [], "state")],
            PreviewFingerprint: revised ? "revised-preview" : "earlier-preview");
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == ReviseTool),
                Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(ReviseTool, ReviseTool, AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: preview)));
    }

    /// <summary>
    /// The turn only reads an earlier preview beside a rename that the ownership check turns down. The earlier preview
    /// is not a prepared write of the turn: the denial keeps the base path, and the preview stays approvable.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AnEarlierPreviewThatTheTurnOnlyReadIsNotAPreparedWriteOfTheTurn(string language)
    {
        var run = await RunRequestTurnAsync(language, RenameOneMessage(language),
            children => Calls(InspectEarlierPreview(), RenameOneUnknown(children)), _ => [Text(language), Text(language)],
            children => ArrangeEarlierPreview(children, revised: false));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        _pendingOperationStore.DidNotReceive().Cancel(UserId, EarlierPreviewId, Arg.Any<string>());
        var denied = run.ToolResults[0].Should().ContainSingle(result => result.Id == "rename-one").Subject;
        JsonSerializer.Serialize(denied.Payload).Should().NotContain("correction_allowed", "no write of the turn prepared");
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language)).And.NotBe(ToolFailureFor(language));
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(EarlierPreviewId);
    }

    /// <summary>
    /// The turn revises an earlier preview in place beside a rename that the ownership check turns down, and the
    /// correction never comes. The earlier preview gets its state from before the turn back, is never cancelled, and
    /// the answer says that it still waits.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AnEarlierPreviewThatTheTurnRevisedGetsItsEarlierStateBack(string language)
    {
        var run = await RunRequestTurnAsync(language, RenameOneMessage(language),
            children => Calls(ReviseEarlierPreview(), RenameOneUnknown(children)), _ => [Text(language), Text(language)],
            children => ArrangeEarlierPreview(children, revised: true));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        _pendingOperationStore.DidNotReceive().Cancel(UserId, EarlierPreviewId, Arg.Any<string>());
        _pendingOperationStore.Received(1).Restore(UserId, EarlierPreviewId,
            AgentOperationFingerprint.Compute("update_habit", EarlierPreviewArguments(run.Children, RevisedTitle).GetRawText()),
            EarlierRevision(run.Children));
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the revised card is not what the store holds");
        run.Result.Value.AiMessage.Should().Be(EarlierKeptFor(language));
    }

    /// <summary>
    /// The turn reads an earlier preview, prepares a delete and gets a rename turned down, and the correction never
    /// comes. The turn withdraws only its own delete. The earlier preview stays in the answer, and the answer says
    /// that it still waits instead of saying that nothing waits.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AWithdrawnTurnKeepsTheEarlierPreviewItReadAndSaysItStillWaits(string language)
    {
        var run = await RunRequestTurnAsync(language, RequestMessage(language),
            children => Calls(InspectEarlierPreview(), Run4Delete(children), RenameOneUnknown(children)),
            _ => [Text(language), Text(language)], children => ArrangeEarlierPreview(children, revised: false));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().Contain(request => request.OperationId == "bulk_delete_habits", "the delete prepared");
        _pendingOperationStore.DidNotReceive().Cancel(UserId, EarlierPreviewId, Arg.Any<string>());
        run.Result.Value.PendingOperations.Should().ContainSingle("the turn withdrew its own delete").Which.Id.Should().Be(EarlierPreviewId);
        run.Result.Value.AiMessage.Should().Be(EarlierKeptFor(language));
    }

    /// <summary>
    /// A preview from an earlier turn renames Water 3 to the title of the request and awaits approval when the turn
    /// starts. The turn reads it, and a bulk rename of the turn covers it.
    /// </summary>
    private void ArrangeCoveredEarlierPreview(Habit[] children)
    {
        var stored = new PendingAgentOperationExecution(EarlierPreviewId, "habits.update", "update_habit",
            EarlierPreviewArguments(children, RenameTitle), AgentExecutionSurface.Chat,
            AgentConfirmationRequirement.FreshConfirmation, "earlier-preview");
        _pendingOperationStore.GetAwaitingAsync(UserId, Arg.Any<CancellationToken>()).Returns([stored]);
        _pendingOperationStore.GetExecution(UserId, EarlierPreviewId).Returns(stored);
        _pendingOperationStore.Cancel(UserId, EarlierPreviewId, Arg.Any<string>()).Returns(true);
        var title = new PendingOperationChange(children[2].Id, children[2].Title, "title", children[2].Title, RenameTitle, "string");
        var preview = new PendingAgentOperation(EarlierPreviewId, "habits.update", "update_habit", "update_habit", AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(10), [title], ChangeTargetCount: 1,
            Items: [new PendingOperationItem("0", children[2].Id, children[2].Title, [title], "state")],
            PreviewFingerprint: "earlier-preview");
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == ReviseTool),
                Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(ReviseTool, ReviseTool, AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: preview)));
    }

    private static AiToolCall DeleteUnknown() => new("delete_habit", "delete-unknown",
        JsonSerializer.SerializeToElement(new { habit_id = UnknownHabitId }));

    /// <summary>
    /// The bulk rename of the turn covers the earlier rename of Water 3, and a delete is turned down. The withdrawn turn
    /// closes nothing that the person had: the earlier preview stays approvable and stays in the answer.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AWithdrawnTurnLeavesTheEarlierPreviewThatItsBulkWriteCovered(string language)
    {
        var run = await RunRequestTurnAsync(language, RequestMessage(language),
            children => Calls(InspectEarlierPreview(), CorrectedRename(children), DeleteUnknown()),
            _ => [Text(language), Text(language)], ArrangeCoveredEarlierPreview);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Held.Should().Contain(request => request.OperationId == "bulk_update_habits", "the bulk rename prepared");
        _pendingOperationStore.DidNotReceive().Cancel(UserId, EarlierPreviewId, Arg.Any<string>());
        run.Result.Value.PendingOperations.Should().ContainSingle("the turn withdrew its own rename").Which.Id.Should().Be(EarlierPreviewId);
        run.Result.Value.AiMessage.Should().Be(EarlierKeptFor(language));
    }

    /// <summary>
    /// The same bulk rename in a turn that ends with its answer still closes the earlier preview that it covers, so the
    /// person never approves the rename of Water 3 twice.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ATurnThatEndsWithItsAnswerClosesTheEarlierPreviewThatItsBulkWriteCovered(string language)
    {
        var run = await RunRequestTurnAsync(language, RequestMessage(language),
            children => Calls(InspectEarlierPreview(), CorrectedRename(children), Run4Delete(children)),
            _ => [Text(language), Text(language)], ArrangeCoveredEarlierPreview);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        _pendingOperationStore.Received(1).Cancel(UserId, EarlierPreviewId,
            AgentOperationFingerprint.Compute("update_habit", EarlierPreviewArguments(run.Children, RenameTitle).GetRawText()));
        run.Result.Value.PendingOperations.Should().NotBeNullOrEmpty().And.NotContain(pending => pending.Id == EarlierPreviewId);
    }

    /// <summary>
    /// The same bulk rename, but the continuation after it fails and the turn ends with the provider error instead of
    /// its answer. The earlier preview that the rename covers stays approvable.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ATurnThatEndsWithAProviderErrorLeavesTheEarlierPreviewThatItsBulkWriteCovered(string language)
    {
        var run = await RunRequestTurnAsync(language, RequestMessage(language),
            children => Calls(InspectEarlierPreview(), CorrectedRename(children), Run4Delete(children)),
            _ => [], ArrangeCoveredEarlierPreview, lastReply: FailedReply("unavailable"));

        run.Result.ErrorCode.Should().Be(ErrorCodes.AiUnavailable, "the turn ends with the provider error");
        run.Held.Should().Contain(request => request.OperationId == "bulk_update_habits", "the bulk rename prepared");
        _pendingOperationStore.DidNotReceive().Cancel(UserId, EarlierPreviewId, Arg.Any<string>());
    }
}
