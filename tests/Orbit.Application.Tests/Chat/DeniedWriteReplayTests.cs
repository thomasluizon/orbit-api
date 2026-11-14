using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
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
    /// model rounds as recorded replies. The block store keeps the answer checkpoint and the answer, as the real store
    /// does, so a turn that fails replays its checkpoint. <paramref name="first"/> replaces the first reply of run 4,
    /// and <paramref name="lastReply"/> follows the recorded replies.
    /// </summary>
    private async Task<DeniedWriteRun> RunDeniedAnswerTurnAsync(string language, Func<Habit[], AiResponse[]> continuations,
        Action<Habit[]>? arrange = null, Func<Habit[], AiResponse>? first = null, Task<Result<AiResponse>>? lastReply = null)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        var (parent, children) = SetupSameTitleChildren("Water - 710ml", 6);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(delete, update);
        var held = HoldReplayWrites(delete, update);
        DenyUnownedHabitTargets([parent, .. children]);
        var block = RetryBlock(RecordQuestion(children, language == "en"
            ? "There are 6 Water - 710ml, not 7. Which two do you want to delete?"
            : "Há 6 Water - 710ml, não 7. Quais dois você quer excluir?"));
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(first?.Invoke(children) ?? Calls(Run4Rename(children), Run4Delete(children))));
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        var replies = Replies(continuations(children), lastReply);
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

    private static Task<Result<AiResponse>>[] Replies(AiResponse[] recorded, Task<Result<AiResponse>>? lastReply) =>
        [.. recorded.Select(reply => Task.FromResult(Result.Success(reply))), .. lastReply is null ? [] : new[] { lastReply }];

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
    private Task<DeniedWriteRun> RunDeniedRequestTurnAsync(string language, Func<Habit[], AiToolCall> second,
        Func<Habit[], AiResponse[]> continuations, (AgentOperationStatus Status, string Reason)? secondOutcome = null) =>
        RunRequestTurnAsync(language, RequestMessage(language), children => Calls(Run4Delete(children), second(children)),
            continuations, children =>
            {
                var name = second(children).Name;
                if (secondOutcome is var (status, reason))
                    _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == name),
                            Arg.Any<CancellationToken>())
                        .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(name, name,
                            AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, status, PolicyReason: reason, Access: "write"),
                            PolicyDenial: status == AgentOperationStatus.Failed ? null : new AgentPolicyDenial(name,
                                name, AgentRiskClass.Low, AgentConfirmationRequirement.FreshConfirmation, reason)));
            });

    private static string RequestMessage(string language) => language == "en"
        ? "delete Water 5 and Water 6, and rename the other 4 to Water - 1.18L"
        : "apaga Water 5 e Water 6, e os outros 4 coloca Water - 1.18L";

    /// <summary>
    /// A turn without a card over six sub-habits with distinct titles. <c>query_habits</c> succeeds with the six
    /// sub-habits, each write prepares unless a target is not one of the person's records, and the model rounds are
    /// recorded replies. <paramref name="lastReply"/> follows the recorded replies.
    /// </summary>
    private async Task<DeniedWriteRun> RunRequestTurnAsync(string language, string message, Func<Habit[], AiResponse> first,
        Func<Habit[], AiResponse[]> continuations, Action<Habit[]>? arrange = null, string? confirmationToken = null,
        Task<Result<AiResponse>>? lastReply = null)
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
        var deleteOne = new DeleteHabitTool(_mediator, _habitRepo);
        var summary = new SetAiSummaryTool(_mediator);
        const string query = "query_habits";
        var handler = CreateHandler(ReadTool(query, new ToolResult(true)), ReadTool(ReviseTool, new ToolResult(true)),
            delete, update, updateOne, deleteOne, summary);
        var held = HoldReplayWrites(delete, update, updateOne, deleteOne, summary);
        DenyUnownedHabitTargets([parent, .. children]);
        var read = new AgentExecuteOperationResponse(new AgentOperationResult(query, query, AgentRiskClass.Low,
            AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded,
            Payload: new { habits = children.Select(child => new { id = child.Id, title = child.Title }) }));
        _operationExecutor.ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == query),
                Arg.Any<CancellationToken>())
            .Returns(read);
        arrange?.Invoke(children);
        SetupAiResponse(first(children));
        var toolResults = new List<IReadOnlyList<AiToolCallResult>>();
        var replies = Replies(continuations(children), lastReply);
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => toolResults.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(replies[0], replies[1..]);
        var result = await handler.Handle(new ProcessUserChatCommand(UserId, message, ConfirmationToken: confirmationToken,
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
        Func<Habit[], AiToolCall> second = form == "bulk" ? Run4Rename : children => Rename("rename-one", OneCharacterOff(children[1].Id));
        var corrected = await RunDeniedRequestTurnAsync(language, second, children => [Calls(CorrectedRename(children)), Text(language)]);

        corrected.Result.IsSuccess.Should().BeTrue(corrected.Result.Error);
        corrected.ToolResults[0].Should().ContainSingle(result => !result.Success).Which.Error
            .Should().Contain(form == "bulk" ? CutShort(corrected.Children[1].Id) : OneCharacterOff(corrected.Children[1].Id));
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
        _ = _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(value => sent = value),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());

        var run = await RunDeniedAnswerTurnAsync(language, children => [Calls(CorrectedRename(children)), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        sent!.DynamicContext.Should().Contain("Validated choice selection: ");
        foreach (var child in run.Children)
            sent.DynamicContext.Should().Contain(child.Id.ToString());
    }

    private static AiToolCall ReadWater(int index) => new("query_habits", $"read-{index}",
        JsonSerializer.SerializeToElement(new { search = $"Water {index}" }));

    private static AiToolCall RenameFirst(Habit[] children) => new("update_habit", "rename-one-again",
        JsonSerializer.SerializeToElement(new { habit_id = children[0].Id, title = RenameTitle }));

    private static string RenameOneMessage(string language) => language == "en"
        ? "rename Water 1 to Water - 1.18L"
        : "muda o nome do Water 1 para Water - 1.18L";

    private static string ToolFailureFor(string language) =>
        language == "en" ? EnglishToolFailureMessage : PortugueseToolFailureMessage;

    /// <summary>
    /// The ownership check turns down the only write of the turn, whose id is the Water 1 id one character off, so no
    /// preview can hold half of the request. The model reads the habits and sends the write again with the right id.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ALoneDenialLetsTheModelReadAndSendTheWriteAgain(string language)
    {
        var run = await RunRequestTurnAsync(language, RenameOneMessage(language),
            children => Calls(Rename("rename-one", OneCharacterOff(children[0].Id))),
            children => [Calls(ReadWater(1)), Calls(RenameFirst(children)), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().NotBe(ToolFailureFor(language)).And.NotBe(NotPreparedFor(language));
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be(run.Children[0].Id);
        var denied = run.ToolResults[0].Should().ContainSingle().Subject;
        denied.Error.Should().Contain(OneCharacterOff(run.Children[0].Id));
        JsonSerializer.Serialize(denied.Payload).Should().NotContain("correction_allowed");
        run.ToolResults[1].Should().ContainSingle().Which.Success.Should().BeTrue();
        run.Result.Value.PolicyDenials.Should().BeNullOrEmpty("the rename on the right id replaced the denied rename");
        run.Result.Value.Actions.Should().NotContain(action => action.Status == ActionStatus.Failed);
    }

    /// <summary>
    /// The model answers in its own words after a lone denial. The answer stays in a turn with no limit, in a turn
    /// that sends a confirmation token, and in a turn at the token budget or at the round limit.
    /// </summary>
    [Theory]
    [InlineData("en", "none")]
    [InlineData("pt-BR", "none")]
    [InlineData("en", "confirmation")]
    [InlineData("pt-BR", "budget")]
    [InlineData("en", "rounds")]
    public async Task DeniedWrite_ALoneDenialKeepsTheAnswerInTheModelsOwnWords(string language, string limit)
    {
        var answer = new AiResponse
        {
            TextMessage = language == "en"
                ? "I couldn't find that habit. Which one do you mean?"
                : "Não achei esse hábito. Qual deles você quer dizer?"
        };
        Func<Habit[], AiResponse> first = limit switch
        {
            "budget" => children => Calls(RenameOneUnknown(children)) with { RequestBudgetTokenCount = AppConstants.MaxAiRequestTotalTokens },
            "rounds" => _ => Calls(ReadWater(1)),
            _ => children => Calls(RenameOneUnknown(children))
        };

        var run = await RunRequestTurnAsync(language, RenameOneMessage(language), first, children => limit == "rounds"
                ? [Calls(ReadWater(2)), Calls(ReadWater(3)), Calls(ReadWater(4)), Calls(RenameOneUnknown(children)), answer]
                : [answer],
            confirmationToken: limit == "confirmation" ? "confirmation-token" : null);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().Be(answer.TextMessage);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// A lone denial that the model never sends again, beside a write that prepares in a later round, leaves half of
    /// the request. The end of the loop withdraws the preview and says so.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ALoneDenialWithASiblingThatPreparesInALaterRoundLeavesNoPreview(string language)
    {
        var run = await RunRequestTurnAsync(language, RequestMessage(language), children => Calls(RenameOneUnknown(children)),
            children => [Calls(Run4Delete(children)), Text(language), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the rename half of the request was not prepared");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// The pending operation store keeps each preview that the executor prepares as awaiting until a cancel closes it,
    /// as the real store does. The plain substitute reads no preview as awaiting, so the recovery after a provider error
    /// drops each preview as stale and hides a preview that holds half of the request.
    /// </summary>
    private void StorePreparedPreviews()
    {
        var closed = new HashSet<Guid>();
        PendingAgentOperationExecution? Stored(Guid id) => !closed.Contains(id) && _heldPreviews.TryGetValue(id, out var held)
            ? new(id, held.Preview.CapabilityId, held.Request.OperationId, held.Request.Arguments, AgentExecutionSurface.Chat,
                held.Preview.ConfirmationRequirement, held.Preview.PreviewFingerprint)
            : null;
        _pendingOperationStore.IsAwaitingConfirmation(UserId, Arg.Any<Guid>()).Returns(call => Stored(call.ArgAt<Guid>(1)) is not null);
        _pendingOperationStore.GetExecution(UserId, Arg.Any<Guid>()).Returns(call => Stored(call.ArgAt<Guid>(1)));
        _pendingOperationStore.Cancel(UserId, Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(call => Stored(call.ArgAt<Guid>(1)) is not null && closed.Add(call.ArgAt<Guid>(1)));
    }

    /// <summary>
    /// The continuation after the round that prepares the sibling: no text and no call, as the preview tool result
    /// asks, a provider failure, or a cancellation of the turn.
    /// </summary>
    private static Task<Result<AiResponse>> FailedReply(string failure) => failure switch
    {
        "noOutput" => Task.FromResult(Result.Failure<AiResponse>(ErrorMessages.AiNoOutput)),
        "unavailable" => Task.FromResult(Result.Failure<AiResponse>(ErrorMessages.AiUnavailable)),
        _ => Task.FromException<Result<AiResponse>>(new OperationCanceledException())
    };

    private void ShouldHaveClosedTheDelete()
    {
        var (request, preview) = _heldPreviews.Values.Should()
            .ContainSingle(held => held.Request.OperationId == "bulk_delete_habits", "the sibling delete prepared").Subject;
        _pendingOperationStore.Received(1).Cancel(UserId, preview.Id,
            AgentOperationFingerprint.Compute("bulk_delete_habits", request.Arguments.GetRawText()));
        _pendingOperationStore.IsAwaitingConfirmation(UserId, preview.Id).Should().BeFalse("no approval brings the delete back");
    }

    /// <summary>
    /// The same lone denial and sibling delete, but the continuation after the delete round fails. The turn leaves the
    /// loop with a provider error, and still withdraws the delete, closes it in the store and says so.
    /// </summary>
    [Theory]
    [InlineData("en", "noOutput")]
    [InlineData("pt-BR", "noOutput")]
    [InlineData("en", "unavailable")]
    [InlineData("pt-BR", "unavailable")]
    public async Task DeniedWrite_AProviderErrorAfterTheSiblingRoundStillLeavesNoPreview(string language, string failure)
    {
        var run = await RunRequestTurnAsync(language, RequestMessage(language), children => Calls(RenameOneUnknown(children)),
            children => [Calls(Run4Delete(children))], _ => StorePreparedPreviews(), lastReply: FailedReply(failure));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the rename half of the request was not prepared");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
        ShouldHaveClosedTheDelete();
    }

    private static string CommittedFor(string language) => language == "en"
        ? "I couldn't finish my reply, and nothing changes until you approve."
        : "Não consegui terminar minha resposta, e nada muda até você aprovar.";

    /// <summary>
    /// The answer turn of run 4 with the cut-short rename alone in the first round, so the denial keeps the base path,
    /// and the delete in a later round. The delete writes the answer checkpoint, and then the continuation fails. The
    /// turn withdraws the delete and closes it in the store, so neither the answer nor a replay of the checkpoint holds
    /// it. A cancelled turn has no answer of its own, and the block answer replays the checkpoint without the delete.
    /// </summary>
    [Theory]
    [InlineData("en", "noOutput")]
    [InlineData("pt-BR", "noOutput")]
    [InlineData("en", "cancel")]
    [InlineData("pt-BR", "cancel")]
    public async Task DeniedWrite_AnAnswerTurnThatFailsAfterTheSiblingRoundLeavesNoPreview(string language, string failure)
    {
        var run = await RunDeniedAnswerTurnAsync(language, children => [Calls(Run4Delete(children))], _ => StorePreparedPreviews(),
            first: children => Calls(Run4Rename(children)), lastReply: FailedReply(failure));

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the rename half of the request was not prepared");
        run.Result.Value.AiMessage.Should().Be(failure == "cancel" ? CommittedFor(language) : NotPreparedFor(language));
        ShouldHaveClosedTheDelete();
    }

    /// <summary>
    /// The rename of one of six same-titled sub-habits waits for the person's choice, and a second rename in the same
    /// round names an id that is not one of the person's records. No write prepared, so the round goes back to the
    /// model, the denied rename keeps its own reason, and the turn asks the choice.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ALoneDenialBesideAWriteThatWaitsForAChoiceStillAsksTheChoice(string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        var (parent, children) = SetupSameTitleChildren("Water - 710ml", 6);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var update = new BulkUpdateHabitsTool(_mediator);
        var updateOne = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var handler = CreateHandler(query, update, updateOne);
        var held = HoldSelectionReplayWrites(query, update, updateOne);
        DenyUnownedHabitTargets([parent, .. children]);
        SetupAiResponse(Calls(new AiToolCall(query.Name, "lookup", JsonSerializer.SerializeToElement(new
        {
            search = "Water 710ml",
            for_selection = false,
            orbit_read_complete = false
        }))));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Calls(RenameCall(children[..1]), RenameOneUnknown(children))), Result.Success(Text(language)));

        var result = await handler.Handle(new(UserId, language == "en"
                ? "rename the Water - 710ml habit to Water - 1.18L"
                : "troque o título do hábito Water 710ml para Water - 1.18L",
            ClientContext: new(SupportsParts: true, PartKinds: ["habitList", "choice"], SupportsHabitListCard: true,
                SupportsDatedHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().BeEmpty();
        result.Value.AiMessage.Should().Be(SelectionQuestionFor("Water - 710ml", language));
        result.Value.Actions.Should().ContainSingle(action => action.Type == "UpdateHabit")
            .Which.MessageKey.Should().Be("chat.toolResult.policyDenied", "the denial is not a tool failure");
        await _aiIntentService.Received(2).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A lone denial in the first round does not take the correction of a later write that the argument check turned
    /// down before dispatch. That write gets its repair, and the rename that never came back still leaves no preview.
    /// </summary>
    [Fact]
    public async Task DeniedWrite_ALoneDenialDoesNotBlockTheRepairOfALaterRejectedWrite()
    {
        SetupUserAndPayGate();
        var create = RecoveryCreateHabitTool();
        var updateOne = new UpdateHabitTool(_habitRepo, _userDateService, _mediator);
        var handler = CreateHandler(create, updateOne);
        var gateway = InstallCorrectionGateway(create, hold: true, updateOne);
        DenyUnownedHabitTargets([]);
        SetupAiResponse(Calls(RenameOneUnknown([])));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Calls(new AiToolCall(create.Name, "rejected", ParseArguments(WithMinuteSeconds(ProductionAdaptationArguments))))),
                Result.Success(Calls(new AiToolCall(create.Name, "repaired", ParseArguments(ProductionAdaptationArguments)))),
                Result.Success(Text("en")));

        var result = await handler.Handle(new ProcessUserChatCommand(UserId,
            "Rename Water to Water - 1.18L and create the adaptation habit at 21:00."), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        gateway.Requests.Where(request => request.OperationId == create.Name).Should().HaveCount(2, "the rejected create gets its repair");
        gateway.Responses[0].Should().BeOfType<RejectedBeforeDispatchResponse>();
        result.Value.PendingOperations.Should().BeNullOrEmpty("the rename half of the request was not prepared");
        result.Value.AiMessage.Should().Be(NotPreparedFor("en"));
    }

    private static string TwoRenamesMessage(string language) => language == "en"
        ? "rename Water 1 to Water - 1.18L and Water 2 to Water - 2L"
        : "muda o nome do Water 1 para Water - 1.18L e do Water 2 para Water - 2L";

    private static bool PreparedFor(DeniedWriteRun run, string operation, Habit child) => run.Held.Any(request =>
        request.OperationId == operation && request.Arguments.TryGetProperty("habit_id", out var id) && id.GetGuid() == child.Id);

    /// <summary>
    /// The model sends the Water 1 rename alone with an id of no record, then the Water 2 rename in the next round.
    /// The Water 2 rename has another change, so it is not the denied write sent again: the denial stays, and the
    /// turn holds no preview with only Water 2.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ALoneDenialStaysWhenALaterWriteRenamesAnotherRecordOfTheRequest(string language)
    {
        var run = await RunRequestTurnAsync(language, TwoRenamesMessage(language), children => Calls(RenameOneUnknown(children)),
            children => [Calls(new AiToolCall("update_habit", "rename-two",
                JsonSerializer.SerializeToElement(new { habit_id = children[1].Id, title = "Water - 2L" }))), Text(language), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        PreparedFor(run, "update_habit", run.Children[1]).Should().BeTrue("the Water 2 rename prepared in the second round");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("a preview with only Water 2 holds half of the request");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// A delete has no change to compare, so a later delete of another record never replaces a denied delete.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ALoneDeniedDeleteStaysWhenALaterDeleteRemovesAnotherRecord(string language)
    {
        var run = await RunRequestTurnAsync(language, language == "en" ? "delete Water 1 and Water 2" : "apaga o Water 1 e o Water 2",
            _ => Calls(new AiToolCall("delete_habit", "delete-one", JsonSerializer.SerializeToElement(new { habit_id = UnknownHabitId }))),
            children => [Calls(new AiToolCall("delete_habit", "delete-two",
                JsonSerializer.SerializeToElement(new { habit_id = children[1].Id }))), Text(language), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        PreparedFor(run, "delete_habit", run.Children[1]).Should().BeTrue("the Water 2 delete prepared in the second round");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("a preview with only Water 2 holds half of the request");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// Beside a prepared delete, the correction of a denied rename sends a rename with another title. It is not the
    /// denied write sent again, so it binds to no denial and the turn holds no preview.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ACorrectionWithAnotherChangeDoesNotReplaceTheDenial(string language)
    {
        var run = await RunDeniedRequestTurnAsync(language, RenameOneUnknown, children => [Calls(new AiToolCall("update_habit",
            "rename-other", JsonSerializer.SerializeToElement(new { habit_id = children[0].Id, title = "Water - 2L" }))), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        PreparedFor(run, "update_habit", run.Children[0]).Should().BeFalse("the candidate binds to no denial and never runs");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the rename half of the request was not prepared");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// The request renames two records to one title. The first rename prepares and the second is turned down. The
    /// correction repeats the prepared rename, which prepares nothing, so the denial stays and the turn holds no
    /// preview with only the first record.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ACorrectionThatRepeatsAPreparedWriteDoesNotReplaceTheDenial(string language)
    {
        var run = await RunRequestTurnAsync(language, language == "en"
                ? "rename Water 1 and Water 2 to Water - 1.18L"
                : "muda o nome do Water 1 e do Water 2 para Water - 1.18L",
            children => Calls(RenameFirst(children), RenameOneUnknown(children)),
            children => [Calls(RenameFirst(children) with { Id = "repeat" }), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        JsonSerializer.Serialize(run.ToolResults[0].Single(result => result.Id == "rename-one").Payload).Should().Contain("correction_allowed");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("a preview with only Water 1 holds half of the request");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    private static string SameRenameMessage(string language) => language == "en"
        ? "rename Water 1 and Water 2 to Water - 1.18L"
        : "muda o nome do Water 1 e do Water 2 para Water - 1.18L";

    private static AiToolCall Rename(string id, string target) => new("update_habit", id,
        JsonSerializer.SerializeToElement(new { habit_id = target, title = RenameTitle }));

    /// <summary>A bulk rename by id, the form in which the model cut the run 4 id short.</summary>
    private static AiToolCall BulkRename(string id, string target) => new("bulk_update_habits", id, JsonSerializer.SerializeToElement(new
    {
        filter = new { all = false, habit_ids = new[] { target }, is_completed = false },
        updates = new { title = RenameTitle }
    }));

    /// <summary>The id with one hex digit changed, the way a copy goes one character off.</summary>
    private static string OneCharacterOff(Guid id)
    {
        var text = id.ToString();
        return text[..10] + (text[10] == 'a' ? 'b' : 'a') + text[11..];
    }

    /// <summary>
    /// The request renames Water 1 and Water 2 to one title. The Water 1 rename names an id of no record and is turned
    /// down alone, and the next round renames Water 2 to the same title. The Water 2 rename has no evidence that it sends
    /// the Water 1 rename again, so the denial stays and the turn holds no preview with only Water 2.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ALoneDenialStaysWhenALaterWriteMakesTheSameChangeOnAnotherRecord(string language)
    {
        var run = await RunRequestTurnAsync(language, SameRenameMessage(language), children => Calls(RenameOneUnknown(children)),
            children => [Calls(Rename("rename-two", children[1].Id.ToString())), Text(language), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        PreparedFor(run, "update_habit", run.Children[1]).Should().BeTrue("the Water 2 rename prepared in the second round");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("a preview with only Water 2 holds half of the request");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// The Water 1 rename prepares and the second rename is turned down. The correction renames Water 3 to the same
    /// title with no evidence that it sends the denied rename again, so it binds to no denial.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ACorrectionWithTheSameChangeOnAnotherRecordDoesNotReplaceTheDenial(string language)
    {
        var run = await RunRequestTurnAsync(language, SameRenameMessage(language),
            children => Calls(RenameFirst(children), RenameOneUnknown(children)),
            children => [Calls(Rename("rename-three", children[2].Id.ToString())), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        PreparedFor(run, "update_habit", run.Children[2]).Should().BeFalse("the candidate binds to no denial and never runs");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("the Water 2 half of the request was not prepared");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// The denied id is the Water 1 id cut short. That relation links only a resend that names Water 1, so a later
    /// rename of Water 2 to the same title leaves the denial in place.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ACutShortIdLinksOnlyTheRecordThatItWasCopiedFrom(string language)
    {
        var run = await RunRequestTurnAsync(language, SameRenameMessage(language),
            children => Calls(BulkRename("rename-one", CutShort(children[0].Id))),
            children => [Calls(Rename("rename-two", children[1].Id.ToString())), Text(language), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("a preview with only Water 2 holds half of the request");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }

    /// <summary>
    /// A denied id that is the Water 1 id cut short or one character off is a copy of exactly one record. A later
    /// rename of Water 1 with the same change sends the denied rename again and replaces the denial.
    /// </summary>
    [Theory]
    [InlineData("en", "cut")]
    [InlineData("pt-BR", "cut")]
    [InlineData("en", "one-off")]
    [InlineData("pt-BR", "one-off")]
    public async Task DeniedWrite_AnIdCopiedFromOneRecordLinksTheResendOfThatRecord(string language, string copy)
    {
        var run = await RunRequestTurnAsync(language, RenameOneMessage(language),
            children => Calls(BulkRename("rename-one", copy == "cut" ? CutShort(children[0].Id) : OneCharacterOff(children[0].Id))),
            children => [Calls(RenameFirst(children)), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        run.Result.Value.AiMessage.Should().NotBe(ToolFailureFor(language)).And.NotBe(NotPreparedFor(language));
        run.Result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle()
            .Which.EntityId.Should().Be(run.Children[0].Id);
        run.Result.Value.PolicyDenials.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// The model invents an id for the Water 1 rename, reads Water 1 and sends the rename again with its id. An id of no
    /// record was copied from no habit, so nothing shows that the later rename sends the denied rename again: the
    /// denial stays and the turn holds no preview.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task DeniedWrite_ALaterWriteNeverReplacesTheDenialOfAnIdThatNoHabitWasCopiedTo(string language)
    {
        var run = await RunRequestTurnAsync(language, RenameOneMessage(language), children => Calls(RenameOneUnknown(children)),
            children => [Calls(ReadWater(1)), Calls(RenameFirst(children)), Text(language), Text(language)]);

        run.Result.IsSuccess.Should().BeTrue(run.Result.Error);
        PreparedFor(run, "update_habit", run.Children[0]).Should().BeTrue("the Water 1 rename prepared in the third round");
        run.Result.Value.PendingOperations.Should().BeNullOrEmpty("no evidence links the rename to the denied rename");
        run.Result.Value.AiMessage.Should().Be(NotPreparedFor(language));
    }
}
