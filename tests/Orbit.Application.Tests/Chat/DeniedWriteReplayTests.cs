using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// Run 4 of the staging capture on the api#1110 build (thomasluizon/orbit-tickets#2129). The person answered a
/// <c>multipleChoice</c> card with the last two of six "Water - 710ml" sub-habits. The model sent a rename with two of
/// the four other ids, the second one cut short, beside a delete with the two selected ids. The ownership check turned
/// the rename down, and the person saw a preview that held only the delete.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private const string RenameTitle = "Water - 1.18L";

    /// <summary>The second rename id of run 4, written short the same way: "dab2ad98-e89-4a8f-a8c6-91d".</summary>
    private static string CutShort(Guid id)
    {
        var text = id.ToString();
        return $"{text[..8]}-{text[9..12]}-{text[14..18]}-{text[19..23]}-{text[24..27]}";
    }

    private static AiToolCall Run4Rename(Habit[] children) => new("bulk_update_habits", "rename", JsonSerializer.SerializeToElement(new
    {
        filter = new { all = false, habit_ids = new[] { children[0].Id.ToString(), CutShort(children[1].Id) }, is_completed = false },
        updates = new { title = RenameTitle }
    }));

    private static AiToolCall Run4Delete(Habit[] children) => new("bulk_delete_habits", "delete",
        JsonSerializer.SerializeToElement(new { habit_ids = children[4..].Select(child => child.Id) }));

    private static AiToolCall CorrectedRename(Habit[] children) => new("bulk_update_habits", "rename-corrected",
        JsonSerializer.SerializeToElement(new
        {
            filter = new { all = false, habit_ids = children[..4].Select(child => child.Id), is_completed = false },
            updates = new { title = RenameTitle }
        }));

    private static string NotPreparedFor(string language) => language == "en"
        ? "I couldn't prepare your whole request, so nothing is waiting for your approval. Please send it again."
        : "Não consegui preparar o seu pedido inteiro, então nada ficou esperando a sua aprovação. Mande de novo, por favor.";

    private sealed record DeniedWriteRun(Result<ChatResponse> Result, Habit[] Children,
        List<IReadOnlyList<AiToolCallResult>> ToolResults, List<AgentExecuteOperationRequest> Held);

    /// <summary>
    /// The executor turns down a write whose habit targets hold an id that is not a GUID or not one of the person's
    /// habits, with the reason the real ownership check writes.
    /// </summary>
    private void DenyUnownedHabitTargets(IReadOnlyCollection<Habit> owned)
    {
        var ids = owned.Select(habit => habit.Id).ToHashSet();
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => HasUnownedTarget(request.Arguments, ids)),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<AgentExecuteOperationRequest>();
                var reason = $"target_not_owned:{request.OperationId}:habit";
                return new AgentExecuteOperationResponse(new AgentOperationResult(request.OperationId, request.OperationId,
                    AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Denied,
                    PolicyReason: reason, Access: "write"),
                    PolicyDenial: new AgentPolicyDenial(request.OperationId, request.OperationId, AgentRiskClass.Low,
                        AgentConfirmationRequirement.FreshConfirmation, reason));
            });
    }

    private static bool HasUnownedTarget(JsonElement node, HashSet<Guid> owned) => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject().Any(property => property.Name switch
        {
            "habit_id" => !(property.Value.ValueKind == JsonValueKind.String
                && Guid.TryParse(property.Value.GetString(), out var id) && owned.Contains(id)),
            "habit_ids" => property.Value.EnumerateArray().Any(item =>
                !(Guid.TryParse(item.GetString(), out var id) && owned.Contains(id))),
            _ => HasUnownedTarget(property.Value, owned)
        }),
        JsonValueKind.Array => node.EnumerateArray().Any(item => HasUnownedTarget(item, owned)),
        _ => false
    };

    /// <summary>
    /// The answer turn of run 4: the stored <c>multipleChoice</c> card, the answer with the last two records, and the
    /// model rounds as recorded replies.
    /// </summary>
    private async Task<DeniedWriteRun> RunDeniedAnswerTurnAsync(string language, Func<Habit[], AiResponse[]> continuations,
        Action<Habit[]>? arrange = null)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        _unitOfWork.PassThroughTransactions<Result<PreparedAiBlockAnswer>>();
        var (parent, children) = SetupSameTitleChildren("Water - 710ml", 6);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(delete, update);
        var held = HoldReplayWrites(delete, update);
        DenyUnownedHabitTargets([parent, .. children]);
        var block = StoredRecordQuestion(children, language == "en"
            ? "There are 6 Water - 710ml, not 7. Which two do you want to delete?"
            : "Há 6 Water - 710ml, não 7. Quais dois você quer excluir?");
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ConversationContext = TestConversationContext,
                ToolCalls = [Run4Rename(children), Run4Delete(children)]
            }));
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        var replies = continuations(children).Select(Result.Success).ToArray();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(replies[0], replies[1..]);
        arrange?.Invoke(children);

        var result = await handler.Handle(new(UserId, "Answer", BlockAnswer: new(block.Id, [],
                Answers: [new AiQuestionAnswer("records", [.. children[4..].Select(child => child.Id.ToString())])]),
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice", "question"])), default);
        return new DeniedWriteRun(result, children, toolResults, held);
    }

    private static AiResponse Calls(params AiToolCall[] calls) => new() { ConversationContext = TestConversationContext, ToolCalls = calls };

    private static AiResponse Text(string language) => new() { TextMessage = language == "en" ? "Review the changes." : "Revise as mudanças." };

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_Run4AnswerTurnCorrectsTheRenameOnceAndReachesOnePreviewWithBothActions(string language)
    {
        var run = await RunDeniedAnswerTurnAsync(language, children => [Calls(CorrectedRename(children)), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        var denied = run.ToolResults.Should().NotBeEmpty().And.Subject.First().Should().ContainSingle(result => result.Id == "rename").Subject;
        denied.Success.Should().BeFalse();
        denied.Error.Should().Contain(CutShort(run.Children[1].Id)).And.Contain("validated choice selection")
            .And.NotBe("Policy denied: target_not_owned:bulk_update_habits:habit");
        JsonSerializer.Serialize(denied.Payload).Should().Contain("correction_allowed");
        var items = run.Result.Value.PendingOperations.Should().NotBeNullOrEmpty().And.Subject
            .SelectMany(operation => operation.Items!).ToArray();
        items.Where(item => item.Fields.Any(field => field.Field == "delete")).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[4..].Select(child => child.Id));
        items.Where(item => item.Fields.Any(field => field.Field == "title")).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[..4].Select(child => child.Id));
        run.Result.Value.PolicyDenials.Should().BeNullOrEmpty();
        run.Result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_Run4AnswerTurnWhoseCorrectionFailsLeavesNoPreviewAndSaysSo(string language)
    {
        var run = await RunDeniedAnswerTurnAsync(language, _ => [Text(language), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the rename half of the request was not prepared");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// The delete of run 4 that the turn withdraws is closed in the pending operation store too. The answer checkpoint
    /// already holds that delete, so only a closed row keeps an approval, a replay of the checkpoint, or the pending
    /// previews of the next turn from bringing it back.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_Run4AnswerTurnWhoseCorrectionFailsCancelsTheWithdrawnDeleteInTheStore(string language)
    {
        var run = await RunDeniedAnswerTurnAsync(language, _ => [Text(language), Text(language)], children =>
            _pendingOperationStore.GetExecution(UserId, Arg.Any<Guid>()).Returns(call => new PendingAgentOperationExecution(
                call.ArgAt<Guid>(1), "habits.bulk.delete", "bulk_delete_habits", Run4Delete(children).Args,
                AgentExecutionSurface.Chat, AgentConfirmationRequirement.FreshConfirmation)));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        var checkpointed = _blockStore.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IIssuedAiBlockStore.CheckpointAnswerAsync))
            .SelectMany(call => JsonSerializer.Deserialize<ChatResponse>((string)call.GetArguments()[3]!,
                ChatStreamEvent.SerializerOptions)!.PendingOperations ?? []).ToArray();
        var withdrawn = checkpointed.Should().ContainSingle("the answer checkpoint holds the delete-only preview").Subject;
        _pendingOperationStore.Received(1).Cancel(UserId, withdrawn.Id,
            AgentOperationFingerprint.Compute("bulk_delete_habits", Run4Delete(run.Children).Args.GetRawText()));
    }

    /// <summary>
    /// A request in a turn without a card, over six sub-habits with distinct titles, so no selection guard holds a
    /// write. The first round sends the delete beside a second write, and the continuations are recorded replies.
    /// <paramref name="secondOutcome"/> is the status and reason the executor gives the second write after dispatch.
    /// </summary>
    private async Task<DeniedWriteRun> RunDeniedRequestTurnAsync(string language, Func<Habit[], AiToolCall> second,
        Func<Habit[], AiResponse[]> continuations, (AgentOperationStatus Status, string Reason)? secondOutcome = null)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var parent = CreateHabit("Water");
        var children = Enumerable.Range(1, 6).Select(index => CreateHabit($"Water {index}")).ToArray();
        foreach (var child in children) child.SetParentHabitId(parent.Id);
        SetupSelectionHabits([parent, .. children]);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var updateOne = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var summary = new SetAiSummaryTool(_mediator);
        var handler = CreateHandler(delete, update, updateOne, summary);
        var held = HoldReplayWrites(delete, update, updateOne, summary);
        DenyUnownedHabitTargets([parent, .. children]);
        var secondCall = second(children);
        if (secondOutcome is var (status, reason))
            _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == secondCall.Name),
                    Arg.Any<CancellationToken>())
                .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(secondCall.Name, secondCall.Name,
                    AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, status, PolicyReason: reason, Access: "write"),
                    PolicyDenial: status == AgentOperationStatus.Failed ? null : new AgentPolicyDenial(secondCall.Name,
                        secondCall.Name, AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, reason)));
        SetupAiResponse(Calls(Run4Delete(children), secondCall));
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        var replies = continuations(children).Select(Result.Success).ToArray();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(replies[0], replies[1..]);
        var message = language == "en"
            ? "delete Water 5 and Water 6, and rename the other 4 to Water - 1.18L"
            : "apaga Water 5 e Water 6, e os outros 4 coloca Water - 1.18L";
        var result = await handler.Handle(new ProcessUserChatCommand(UserId, message,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, PartKinds: ["choice", "question"])), default);
        return new DeniedWriteRun(result, children, toolResults, held);
    }

    private static readonly Guid UnknownHabitId = Guid.Parse("5d0c7a43-2f61-4b8e-9c3a-7e1f0b6d2a95");

    /// <summary>A single rename whose id is a GUID of no record of the person.</summary>
    private static AiToolCall RenameOneUnknown(Habit[] children) => new("update_habit", "rename-one",
        JsonSerializer.SerializeToElement(new { habit_id = UnknownHabitId, title = RenameTitle }));

    [Theory]
    [InlineData("en", "bulk")]
    [InlineData("pt-BR", "bulk")]
    [InlineData("en", "single")]
    [InlineData("pt-BR", "single")]
    public async Task DeniedWrite_TurnWithoutACardCorrectsTheDeniedWriteOrLeavesNoPreview(string language, string form)
    {
        Func<Habit[], AiToolCall> second = form == "bulk" ? Run4Rename : RenameOneUnknown;
        var corrected = await RunDeniedRequestTurnAsync(language, second, children => [Calls(CorrectedRename(children)), Text(language)]);

        corrected.Result.IsSuccess.Should().BeTrue(corrected.Result.Error);
        corrected.ToolResults[0].Should().ContainSingle(result => !result.Success).Which.Error
            .Should().Contain(form == "bulk" ? CutShort(corrected.Children[1].Id) : UnknownHabitId.ToString());
        corrected.Result.Value.PendingOperations!.SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo(corrected.Children.Select(child => child.Id));

        var failed = await RunDeniedRequestTurnAsync(language, second, _ => [Text(language), Text(language)]);

        failed.Result.IsSuccess.Should().BeTrue(failed.Result.Error);
        failed.Result.Value.PendingOperations.Should().BeNullOrEmpty();
        failed.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AWriteThatFailsAfterDispatchBesideAPreparedWriteLeavesNoPreview(string language)
    {
        var run = await RunDeniedRequestTurnAsync(language, children => new("update_habit", "rename-one",
                JsonSerializer.SerializeToElement(new { habit_id = children[0].Id, title = RenameTitle })),
            _ => [Text(language), Text(language)], secondOutcome: (AgentOperationStatus.Failed, "execution_failed"));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the rename failed, so the delete alone is half of the request");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language), JsonSerializer.Serialize(run.Result.Value.Actions));
    }

    /// <summary>
    /// A plan, feature or policy denial does not change when the person sends the request again. The turn keeps the
    /// prepared delete, the denied write keeps its own reason, and the reply never asks for a second send.
    /// </summary>
    [Theory]
    [InlineData("en", AgentOperationStatus.Denied, "plan_required:Pro", "policyDenied")]
    [InlineData("pt-BR", AgentOperationStatus.Denied, "plan_required:Pro", "policyDenied")]
    [InlineData("en", AgentOperationStatus.Denied, "feature_disabled:ai_summary", "policyDenied")]
    [InlineData("pt-BR", AgentOperationStatus.Denied, "PAY_GATE", "policyDenied")]
    [InlineData("en", AgentOperationStatus.UnsupportedByPolicy, "unsupported_by_policy", "unsupportedByPolicy")]
    public async Task DeniedWrite_APlanOrPolicyDenialBesideAPreparedWriteKeepsThePreviewAndItsOwnReason(string language,
        AgentOperationStatus status, string reason, string messageKey)
    {
        var explanation = language == "en" ? "The AI summary needs Pro." : "O resumo com IA precisa do Pro.";
        var run = await RunDeniedRequestTurnAsync(language,
            _ => new("set_ai_summary", "summary", JsonSerializer.SerializeToElement(new { enabled = true })),
            _ => [new AiResponse { TextMessage = explanation }, new AiResponse { TextMessage = explanation }],
            secondOutcome: (status, reason));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().NotBe(NotPreparedFor(language));
        run.Result.Value.PendingOperations.Should().NotBeNullOrEmpty("the delete is the allowed part of the request").And.Subject
            .SelectMany(operation => operation.Items!).Select(item => item.EntityId)
            .Should().BeEquivalentTo(run.Children[4..].Select(child => child.Id));
        run.Result.Value.PolicyDenials.Should().ContainSingle().Which.Reason.Should().Be(reason);
        var denied = run.Result.Value.Actions.Should().ContainSingle(action => action.Type == "SetAiSummary").Subject;
        denied.Error.Should().NotBe(NotPreparedFor(language));
        denied.MessageKey.Should().Be($"chat.toolResult.{messageKey}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_AForeignIdGetsTheSameTextAsAnIdThatDoesNotExist(string language)
    {
        var foreign = Guid.NewGuid();
        var missing = Guid.NewGuid();
        string ErrorFor(DeniedWriteRun run) => run.ToolResults[0].Single(result => !result.Success).Error!;
        AiToolCall Rename(Guid id) => new("update_habit", "rename-one",
            JsonSerializer.SerializeToElement(new { habit_id = id, title = RenameTitle }));

        var foreignRun = await RunDeniedRequestTurnAsync(language, _ => Rename(foreign), _ => [Text(language), Text(language)]);
        var missingRun = await RunDeniedRequestTurnAsync(language, _ => Rename(missing), _ => [Text(language), Text(language)]);

        ErrorFor(foreignRun).Replace(foreign.ToString(), "ID").Should().Be(ErrorFor(missingRun).Replace(missing.ToString(), "ID"));
        ErrorFor(foreignRun).Should().Contain(foreign.ToString()).And.Contain("not IDs of this person's records");
        foreignRun.Result.Value.Operations.Should().ContainSingle(operation => operation.Status == AgentOperationStatus.Denied)
            .Which.PolicyReason.Should().Be("target_not_owned:update_habit:habit");
    }

    /// <summary>
    /// Lead 3 of part 3: the answer turn of run 4 already gives the model every candidate id as server text, the
    /// selected ones and the remaining ones, so the cut-short id did not come from a missing id.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_Run4AnswerTurnContextHoldsTheSelectedAndTheRemainingIds(string language)
    {
        AiToolRequest? sent = null;
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(value => sent = value),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());

        var run = await RunDeniedAnswerTurnAsync(language, children => [Calls(CorrectedRename(children)), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        sent!.DynamicContext.Should().Contain("Validated choice selection: ");
        foreach (var child in run.Children)
            sent.DynamicContext.Should().Contain(child.Id.ToString());
    }
}
