using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using System.Text.Json.Nodes;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private static AiToolCall AskWithMissingInput(AiToolCall call, string reason)
    {
        var args = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        var input = args["input"]?.AsObject() ?? args;
        input["missing_input"] = reason;
        return call with { Args = JsonSerializer.SerializeToElement(args) };
    }

    private static AiToolCall AskSelectionRead(string? search = null) => new("query_habits", "ask-selection-read",
        JsonSerializer.SerializeToElement(new { search, for_selection = true, orbit_read_complete = false }));

    private ProcessUserChatCommandHandler CreateRecordAskHandler()
    {
        SetupSelectionHabits(CreateHabit("Water"), CreateHabit("Walk"));
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        return handler;
    }

    private async Task AssertOnlyAskSelectionRead()
    {
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId == "query_habits"), Arg.Any<CancellationToken>());
        await _operationExecutor.DidNotReceive().ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request => request.OperationId != "query_habits"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "habit_setup")]
    [InlineData("pt-BR", "habit_setup")]
    public async Task G12_HabitSetupDoesNotRequireAnAccountRead(string language, string reason)
    {
        SetupExistingParents(language);
        EnableComposition();
        var call = AskWithMissingInput(ToolResponse("ask", "question", ChoiceArguments).ToolCalls!.Single(), reason);
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [call] });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["choice"], Locale: language);

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Block.Should().BeOfType<AstraChoiceBlock>();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        AssertStoredComposition(result.Value, client, language);
    }

    [Fact]
    public async Task G12_HabitSetupCreationDraftDoesNotRequireAnAccountRead()
    {
        SetupUserAndPayGate();
        EnableComposition();
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        SetupAiResponse(ToolResponse("ask", "draft", """{"input":{"missing_input":"habit_setup","form":"pickList","title":"Choose drafts","action":"create_habits","items":[{"id":"walk","label":"Walk","arguments":{"title":"Walk","frequency_unit":"Day","frequency_quantity":1}}]}}"""));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Help me plan.",
            ClientContext: new(SupportsParts: true, PartKinds: ["pickList"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Block.Should().BeOfType<AstraPickListBlock>();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _operationExecutor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        _mediator.ReceivedCalls().Should().OnlyContain(call => call.GetArguments()[0] is CheckChatCommandQuery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G12_RecordQuestionCannotUseAFailedOrUnrelatedRead(bool unrelated)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var read = unrelated ? CompositionTool("get_profile") : ReadTool("query_habits", new ToolResult(false, Error: "Read unavailable."));
        var handler = CreateHandler(read, RealAskTool());
        UseProductionReadExecutor(read);
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls =
        [
            new(read.Name, "unusable-read", ParseArguments("{}")),
            AskWithMissingInput(ToolResponse("ask", "question", ChoiceArguments).ToolCalls!.Single(), "record_selection")
        ]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse()));

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        if (unrelated)
            await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(item => item.Name == "ask" && !item.Success
                    && item.Error!.Contains("Read the account records"))), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        else
        {
            result.Value.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Failed);
            result.Value.AiMessage.Should().Be("I couldn't complete that. Please try again.");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("earlier_message")]
    [InlineData("record_selection")]
    [InlineData("bulk_selection")]
    public async Task G12_UnreadRecordQuestionsAndUnknownReasonsReturnValidationFeedback(string? reason)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var call = ToolResponse("ask", "question", """{"form":"choice","question":"Which record?","options":[{"id":"a","label":"First"},{"id":"b","label":"Second"}]}""").ToolCalls!.Single();
        SetupAiResponse(new AiResponse
        {
            ConversationContext = TestConversationContext,
            ToolCalls = [reason is null ? call : AskWithMissingInput(call, reason)]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Please choose a time." }));

        var result = await CreateHandler(RealAskTool()).Handle(new(UserId, "Continue.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().Be("Please choose a time.");
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && results[0].Error!.Contains("A failed ask did not issue a question")), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", false)]
    [InlineData("pt-BR", true)]
    public async Task G12_ProactiveRecordQuestionWithoutAReadRecoversThroughTheOriginalRequest(string language, bool freeText)
    {
        SetupExistingParents(language);
        EnableComposition();
        var habit = CreateHabit(language == "en" ? "Water" : "Beber água");
        SetupSelectionHabits(habit);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var ask = RealAskTool();
        var handler = CreateHandler(ask, query);
        UseProductionReadExecutor(query);
        SetupAiResponse(ToolResponse(ask.Name, "premature-question", JsonSerializer.Serialize(new
        {
            input = new
            {
                missing_input = "record_selection",
                form = "choice",
                question = language == "en" ? "Which reminder?" : "Qual lembrete?",
                allowFreeText = freeText,
                options = new[] { new { id = "today", label = "Today" }, new { id = "other", label = "Other" } }
            }
        })));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(ToolResponse(query.Name, "read", """{"relative_period":"today","is_completed":false,"for_selection":false,"orbit_read_complete":true}""")));
        var history = new List<ChatHistoryMessage>
        {
            new("assistant", language == "en" ? "You have habits left for today. Want to see what is pending?"
                : "Você ainda tem hábitos para hoje. Quer ver o que falta?")
        };
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList", "choice"],
            SupportsHabitListCard: true, SupportsDatedHabitListCard: true, Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Continue the earlier message.", History: history,
            ClientContext: client, StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var part = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        part.Block.Should().BeOfType<HabitListCard>().Which.Items.Should().ContainSingle().Which.Title.Should().Be(habit.Title);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && results[0].Error!.Contains("Read the account records")), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        var request = (AiToolRequest)_aiIntentService.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "SendWithToolsAsync").GetArguments()[0]!;
        request.History.Should().BeEquivalentTo(history);
        events.Should().NotContain(item => item.Type == "delta");
        AssertStoredComposition(result.Value, client, language);
    }

    [Fact]
    public void G12_AskSchemaRequiresTheMissingInputReasonOnEveryForm()
    {
        var forms = JsonSerializer.SerializeToElement(RealAskTool().GetParameterSchema()).GetProperty("properties")
            .GetProperty("input").GetProperty("anyOf");

        foreach (var form in forms.EnumerateArray())
        {
            form.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Contain("missing_input");
            form.GetProperty("properties").GetProperty("missing_input").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()).Should().BeEquivalentTo("habit_setup", "preference", "check_in", "record_selection", "bulk_selection");
        }
    }
}
