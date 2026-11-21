using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

/// <summary>
/// The owner's exchange of 12 November 2026: seven habits called "Water - 710ml" stated, six found, "keep the first
/// 4" answered, and then the opposite question "which 2 do you delete" asked. The fixtures in
/// <c>Fixtures/ChoiceSettlement</c> hold the model rounds of the exchange. The test runs the production read tool, the
/// real ask tool, the real selection guard and the real previewer over them.
/// </summary>
public partial class ProcessUserChatCommandHandlerTests
{
    private static string OwnerSplitMessage(string language)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AstraDates", "choice-settlement.json")));
        return corpus.RootElement.GetProperty("messages").GetProperty(language).GetString()!;
    }

    private static AiResponse ChoiceSettlementRound(string language, int round, Habit[] children)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChoiceSettlement", $"owner-split-{language}-r{round}.json");
        var text = File.ReadAllText(path);
        for (var index = 0; index < children.Length; index++)
            text = text.Replace($"\"habit-{index + 1}\"", $"\"{children[index].Id}\"", StringComparison.Ordinal);
        using var capture = JsonDocument.Parse(text);
        return new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [.. capture.RootElement.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
                new AiToolCall(call.GetProperty("name").GetString()!, $"settlement-{round}-{index}", call.GetProperty("arguments").Clone()))]
        };
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task ChoiceSettlement_OneAnsweredChoiceSettlesTheOppositeQuestionAndReachesOnePreview(string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        _unitOfWork.PassThroughTransactions<Result<PreparedAiBlockAnswer>>();
        var (_, children) = SetupSameTitleChildren("Water - 710ml", 6);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var delete = new BulkDeleteHabitsTool(_mediator, _habitRepo);
        var update = new BulkUpdateHabitsTool(_mediator);
        var handler = CreateHandler(RealAskTool(), query, delete, update);
        var held = HoldSelectionReplayWrites(query, delete, update);
        var issued = new List<IssuedAiBlock>();
        _blockStore.AddAsync(Arg.Do<IssuedAiBlock>(issued.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var sent = new List<AiToolRequest>();
        _aiIntentService.SendWithToolsAsync(Arg.Do<AiToolRequest>(sent.Add), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ChoiceSettlementRound(language, 1, children)),
                Result.Success(ChoiceSettlementRound(language, 3, children)));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ChoiceSettlementRound(language, 2, children)),
                Result.Success(ChoiceSettlementRound(language, 4, children)),
                Result.Success(ChoiceSettlementRound(language, 5, children)),
                Result.Success(new AiResponse { TextMessage = language == "en" ? "Review the changes." : "Revise as alterações." }));
        var client = new AgentClientContext(SupportsParts: true, SupportsPendingOperationChanges: true,
            PartKinds: ["choice", "question", "habitList"], SupportsHabitListCard: true, Locale: language);

        var first = await handler.Handle(new(UserId, OwnerSplitMessage(language), ClientContext: client), default);

        first.IsSuccess.Should().BeTrue(first.Error);
        var block = issued.Should().ContainSingle("the first question reaches the person").Subject;
        block.ReadDefinition().Title.Should().Contain("6").And.Contain("7", "the message says the count that the read found");
        _blockStore.GetAsync(block.Id, UserId, Arg.Any<CancellationToken>()).Returns(block);
        _blockStore.MarkAnsweredAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call => { block.MarkAnswered(call.ArgAt<DateTime>(2)); return true; });
        _blockStore.CheckpointAnswerAsync(block.Id, UserId, Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var answer = await handler.Handle(new(UserId, "Answer", ClientContext: client, BlockAnswer: new(block.Id, ["first4"])), default);

        answer.IsSuccess.Should().BeTrue(answer.Error);
        issued.Should().ContainSingle("the answer settles the opposite question, so no second card reaches the person");
        answer.Value.Parts?.OfType<ChatBlockPart>().Should().NotContain(part => part.Block is AiChoiceBlock);
        sent[^1].DynamicContext.Should().Contain("Validated choice selection: ").And.Contain("never ask the same decision again from another side");
        held.Select(request => request.OperationId).Should().BeEquivalentTo(["bulk_delete_habits", "bulk_update_habits"]);
        answer.Value.PendingOperations.Should().HaveCount(2);
        var deleteItems = answer.Value.PendingOperations!.Single(operation => operation.CapabilityId == "bulk_delete_habits").Items!;
        deleteItems.Select(item => item.EntityId).Should().BeEquivalentTo(children[4..].Select(child => child.Id));
        var renameItems = answer.Value.PendingOperations!.Single(operation => operation.CapabilityId == "bulk_update_habits").Items!;
        renameItems.Select(item => item.EntityId).Should().BeEquivalentTo(children[..4].Select(child => child.Id));
        held.Single(request => request.OperationId == "bulk_update_habits").Arguments
            .GetProperty("updates").GetProperty("title").GetString().Should().Be("Water - 1.18L");
    }
}
