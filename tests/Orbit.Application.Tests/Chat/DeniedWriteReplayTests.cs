using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
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
    private async Task<DeniedWriteRun> RunDeniedAnswerTurnAsync(string language, Func<Habit[], AiResponse[]> continuations)
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
}
