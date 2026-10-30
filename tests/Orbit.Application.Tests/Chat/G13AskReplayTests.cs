using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    private static IEnumerable<string?> DeclaredAskReasons(AiToolRequest request) => request.ToolDeclarations
        .Select(value => JsonSerializer.SerializeToElement(value)).Where(value => value.GetProperty("name").GetString() == "ask")
        .SelectMany(value => value.GetProperty("parameters").GetProperty("properties").GetProperty("input").GetProperty("anyOf")
        .EnumerateArray()).SelectMany(form => form.GetProperty("properties").GetProperty("missing_input")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

    [Theory]
    [InlineData(1, "en")]
    [InlineData(2, "en")]
    [InlineData(3, "en")]
    [InlineData(1, "pt-BR")]
    [InlineData(2, "pt-BR")]
    [InlineData(3, "pt-BR")]
    public async Task G13_CapturedProactiveAskIsUnavailableBeforeAccountRead(int repeat, string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        var plan = CapturedCompositionPlan($"prod-r4-composition-proactive-pt-BR-r{repeat}");
        SetupAiResponse(plan with { ToolCalls = [plan.ToolCalls![0]] });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CapturedCompositionPlan("prod-r4-composition-proactive-pt-BR-r3") with
            { ToolCalls = [CapturedCompositionPlan("prod-r4-composition-proactive-pt-BR-r3").ToolCalls![1]] }));
        var history = new List<ChatHistoryMessage>
        {
            new("assistant", language == "en" ? "You have habits left for today. Want to see what is pending?"
                : "Você ainda tem hábitos para hoje. Quer ver o que falta?")
        };
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList", "choice", "question"],
            SupportsHabitListCard: true, SupportsDatedHabitListCard: true, Locale: language);
        var events = new List<ChatStreamEvent>();

        var result = await handler.Handle(new(UserId, "Continue.", History: history, ClientContext: client,
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        var request = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<AiToolRequest>()).Single();
        DeclaredAskReasons(request).Should().BeEmpty();
        request.History.Should().BeEquivalentTo(history);
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Block.Should().BeOfType<HabitListCard>();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        events.Should().NotContain(item => item.Type == "delta");
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task G13_CompletedProactiveReadNeedsNoAdditionalModelRound(string language)
    {
        SetupExistingParents(language);
        EnableComposition();
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        var captured = CapturedCompositionPlan("prod-r4-composition-proactive-pt-BR-r3");
        SetupAiResponse(captured with { ToolCalls = [captured.ToolCalls![1]] });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList", "choice", "question"],
            SupportsHabitListCard: true, SupportsDatedHabitListCard: true, Locale: language);

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Block.Should().BeOfType<HabitListCard>();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        await AssertNoCompositionContinuation();
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _operationExecutor.Received(1).ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData("en", "preference", false)]
    [InlineData("pt-BR", "preference", false)]
    [InlineData("en", "check_in", false)]
    [InlineData("pt-BR", "check_in", false)]
    [InlineData("en", "check_in", true)]
    [InlineData("pt-BR", "check_in", true)]
    public async Task G13_PersonOnlyQuestionsUnlockAfterSuccessfulTypedAccountRead(string language, string reason, bool empty)
    {
        SetupExistingParents(language);
        EnableComposition();
        var handler = CreateRecordAskHandler();
        if (empty) SetupSelectionHabits();
        SetupAiResponse(ToolResponse("query_habits", "context", """{"for_selection":false,"orbit_read_complete":false}"""));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var updates = call.Arg<AiConversationContext>().ToolDeclarationUpdates;
                DeclaredAskReasons(new AiToolRequest("", "", updates)).Distinct().Should()
                    .BeEquivalentTo("habit_setup", "preference", "check_in", "record_selection", "bulk_selection");
                return Result.Success(new AiResponse
                {
                    ConversationContext = TestConversationContext,
                    ToolCalls = [AskWithMissingInput(ToolResponse("ask", "question", ChoiceArguments).ToolCalls!.Single(), reason)]
                });
            });
        var client = new AgentClientContext(SupportsParts: true, PartKinds: ["habitList", "choice"],
            SupportsHabitListCard: true, Locale: language);

        var result = await handler.Handle(new(UserId, "Continue.", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Which.Block.Should().BeOfType<AstraChoiceBlock>();
        await AssertOnlyAskSelectionRead();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        AssertStoredComposition(result.Value, client, language);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G13_FeatureHelpAndFailedReadsDoNotUnlockAccountQuestions(bool featureHelp)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var read = featureHelp ? new DescribeFeatureTool(new FeatureExplanationService(), new AppSurfaceCatalog())
            : ReadTool("query_habits", new Orbit.Application.Chat.Tools.ToolResult(false, Error: "Read unavailable."));
        var handler = CreateHandler(read, RealAskTool());
        UseProductionReadExecutor(read);
        var conversation = new AiConversationContext { Messages = new List<object>(), Options = new object() };
        SetupAiResponse(new AiResponse
        {
            ConversationContext = conversation,
            ToolCalls =
            [
                new(read.Name, "read", ParseArguments(featureHelp ? """{"feature_key":"frequencies"}""" : "{}")),
                AskWithMissingInput(ToolResponse("ask", "question", ChoiceArguments).ToolCalls!.Single(), "check_in")
            ]
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Open Today." }));

        var result = await handler.Handle(new(UserId, "Continue.",
            ClientContext: new(SupportsParts: true, PartKinds: ["choice"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts!.OfType<ChatBlockPart>().Should().NotContain(part => part.Kind == "choice");
        conversation.ToolDeclarationUpdates.Should().BeEmpty();
        await _blockStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public void G13_AskReadGuidanceLimitsTheInitialExceptionToNewHabitDrafts()
    {
        const string guidance = "Before a successful typed account read in this turn, ask is unavailable unless a create tool returned a typed missing-field result for a new habit draft. Submit the requested draft to its create tool first, preserving unspecified fields as null; do not invent missing values to avoid that result. Then ask only for the returned missing_field of its draft_call_id. Never create a draft for advice, a reminder, an existing record, a preference or a check-in. Read account records to resolve those requests; when the read answers the request, return it with orbit_read_complete=true and do not ask.";
        var tool = RealAskTool();
        tool.Description.Should().Contain(guidance);
        var forms = JsonSerializer.SerializeToElement(tool.GetParameterSchema(false,
            [new("draft", "Walk", "frequency_unit", Guid.NewGuid())])).GetProperty("properties")
            .GetProperty("input").GetProperty("anyOf");
        foreach (var form in forms.EnumerateArray())
            form.GetProperty("properties").GetProperty("missing_input").GetProperty("description").GetString().Should().Be(guidance);
    }
}
