using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAskRecovery_CapturedNullQuestionPreservesBatchPreview(bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var create = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(RealAskTool(), create);
        var held = HoldReplayWrites(create);
        SetupAiResponse(FailedAskCapture("composition-medical-batch-pt-BR-r3"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(FailedAskCapture("composition-medical-batch-pt-BR-r3", "GateEight")),
                Result.Success(new AiResponse()));

        var result = await handler.Handle(new(UserId, "Prepare the requested items.", ClientContext: new(
            SupportsParts: true, PartKinds: ["question"], SupportsPendingOperationChanges: true, Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().NotBeEmpty();
        held.Should().ContainSingle().Which.OperationId.Should().Be("bulk_create_habits");
        result.Value.Actions.Should().BeEmpty();
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeEmpty();
        await AssertFailedAskReturnedToModel();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAskRecovery_CapturedNullQuestionPreservesCompoundRead(bool supported)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habit = CreateHabit("Beber água");
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns([habit]);
        var goal = Goal.Create(UserId, "Hidratação constante", 10, "sessões").Value;
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        var habits = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var goals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var handler = CreateHandler(RealAskTool(), habits, goals);
        UseProductionReadExecutor(habits, goals);
        var capture = FailedAskCapture("composition-compound-missing-kind-pt-BR-r1");
        SetupAiResponse(capture with { ToolCalls = capture.ToolCalls!.Take(1).ToArray() });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(capture with { ToolCalls = capture.ToolCalls!.Skip(1).ToArray() }),
                Result.Success(new AiResponse()));

        var result = await handler.Handle(new(UserId, "Show the requested records.", ClientContext: new(
            SupportsParts: true, PartKinds: supported ? ["question", "habitList", "goalList"] : ["question"],
            SupportsHabitListCard: true, SupportsGoalListCard: true, Locale: "pt-BR")), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Operations.Should().HaveCount(2).And.OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded);
        result.Value.Operations!.Select(operation => operation.OperationId).Should().Equal("query_habits", "query_goals");
        if (supported)
        {
            result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Kind).Should().Equal("habitList", "goalList");
            result.Value.AiMessage.Should().BeNullOrEmpty();
        }
        else
        {
            result.Value.Parts.Should().HaveCount(2).And.AllBeOfType<ChatTextPart>();
            result.Value.AiMessage.Should().Contain(habit.Title).And.Contain(goal.Title);
        }
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await AssertFailedAskReturnedToModel();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Theory]
    [InlineData("en", "Which time works for you?")]
    [InlineData("pt-BR", "Qual horário funciona para você?")]
    public async Task FailedAskRecovery_TextContinuationIsTheAnswer(string locale, string text)
    {
        SetupUserAndPayGate();
        EnableComposition();
        SetupAiResponse(FailedAskCapture("composition-medical-batch-pt-BR-r3"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = text }));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan.",
            ClientContext: new(SupportsParts: true, PartKinds: ["question"], Locale: locale)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be(text);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>().Which.Text.Should().Be(text);
        await AssertFailedAskReturnedToModel();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedAskRecovery_ExhaustedQuestionRetriesContinueWithoutIssuingABlock()
    {
        SetupUserAndPayGate();
        var invalid = FailedAskCapture("composition-medical-batch-pt-BR-r3");
        SetupAiResponse(invalid);
        const string text = "Which time works for you?";
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(invalid), Result.Success(ToolResponse("ask", "third", ChoiceArguments)),
                Result.Success(new AiResponse { TextMessage = text }));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice", "question"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be(text);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(TestConversationContext,
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && results[0].Id == "third"
                && !results[0].Success && results[0].Error != null && results[0].Error!.Contains("retry limit")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    private async Task AssertFailedAskReturnedToModel() =>
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(TestConversationContext,
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && results[0].Name == "ask"
                && !results[0].Success && results[0].Error != null && results[0].Error!.Contains("questions")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());

    private static AiResponse FailedAskCapture(string name, string folder = "GateNineAsk")
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", folder, name + ".json")));
        return new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = capture.RootElement.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
                new AiToolCall(call.GetProperty("name").GetString()!, "failed-ask-" + index,
                    call.GetProperty("arguments").Clone())).ToArray()
        };
    }
}
