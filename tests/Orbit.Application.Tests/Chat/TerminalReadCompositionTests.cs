using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "Here is your habit ranking.")]
    [InlineData("pt-BR", "Aqui está o ranking dos seus hábitos.")]
    [InlineData("en", "Here is your habit ranking. Another sentence.")]
    [InlineData("pt-BR", "Aqui está o ranking dos seus hábitos. Outra frase.")]
    [InlineData("en", "Here are your top 3 habits.")]
    [InlineData("pt-BR", "Veja seus 3 principais hábitos.")]
    [InlineData("en", "You have one completion.")]
    [InlineData("pt-BR", "Você tem duas conclusões.")]
    [InlineData("en", "Read leads the ranking.")]
    [InlineData("en", "# Your ranking")]
    [InlineData("en", "")]
    public async Task TerminalRead_ComposesFirstSentenceAndProducedRankingWithOneCall(string language, string text)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst, FeatureFlagKeys.AstraEvalTrace,
                FeatureFlagKeys.AstraConversationRecording]);
        var clock = new ChatTimingClock();
        _turnClock = clock;
        var completion = CreateTimedCompletion(clock);
        var ranking = await ProduceRankingAsync();
        var tool = ReadTool("get_habit_ranking", new ToolResult(true, Payload: ranking));
        var events = new List<ChatStreamEvent>();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<AiToolRequest>().DynamicContext.Should().Contain(TerminalReadDirective.Instruction);
            return completion.RunAsync(UserId, _ => Task.FromResult(Result.Success(
                ToolResponse(tool.Name, "ranking", "{\"orbit_read_complete\":true}") with
                { TextMessage = text })), default, "{}");
        });

        var result = await CreateHandler(tool).Handle(new(UserId,
            language == "en" ? "Rank my habits" : "Classifique meus hábitos", EvalTraceRequested: true,
            ClientContext: new(SupportsParts: true, PartKinds: ["habitRanking"]),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.EvalTrace!.Timing!.Phases.Count(phase => phase.Name == "model_call").Should().Be(1);
        result.Value.EvalTrace.ToolCalls.Should().ContainSingle().Which.Status.Should().Be("Succeeded");
        result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Block
            .Should().BeOfType<HabitRankingCard>().Which.Ranking.Should().Be(ranking);
        result.Value.Parts.Should().ContainSingle();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        events.Should().Contain(item => item.Type == "round");
        var recordedJson = System.Text.Json.JsonSerializer.Serialize(result.Value with { EvalTrace = null }, ChatStreamEvent.SerializerOptions);
        _conversationRecorder.Received(1).TryRecord(Arg.Is<ConversationTurnCapture>(turn => turn.ResponseJson == recordedJson));
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task TerminalRead_FailedOrMissingBlockStillRequestsContinuation(string language, bool missingBlock)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        var tool = ReadTool("query_habits", missingBlock ? new ToolResult(true) : new ToolResult(false, Error: "Read failed"));
        SetupAiResponse(ToolResponse(tool.Name, "read", "{\"orbit_read_complete\":true}"));
        ContinueWithReadDirective("No result.");

        var result = await CreateHandler(tool).Handle(new(UserId, "Show habits", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList"], SupportsHabitListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        result.Value.Parts!.OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.AiMessage.Should().Be(language == "en"
            ? "I couldn't complete that. Please try again."
            : "Não consegui concluir isso. Tente novamente.");
    }

    [Theory]
    [InlineData("en", false, false)]
    [InlineData("pt-BR", false, false)]
    [InlineData("en", true, false)]
    [InlineData("pt-BR", true, false)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", true, true)]
    public async Task TerminalRead_UnsupportedClientKeepsReadableFallback(string language, bool parts, bool disabled)
    {
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(disabled ? [FeatureFlagKeys.AstraBlockFirst, FeatureFlagKeys.AstraPartsDisabled] : [FeatureFlagKeys.AstraBlockFirst]);
        var ranking = await ProduceRankingAsync();
        var tool = ReadTool("get_habit_ranking", new ToolResult(true, Payload: ranking));
        SetupAiResponse(ToolResponse(tool.Name, "ranking", "{\"orbit_read_complete\":true}"));
        ContinueWithReadDirective("[[orbit:ranking]]");

        var result = await CreateHandler(tool).Handle(new(UserId, "Rank my habits", ClientContext: new(
            SupportsParts: parts, PartKinds: [])), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().Contain("Read: 1").And.NotContain("[[orbit:");
        result.Value.Parts?.OfType<ChatBlockPart>().Should().BeNullOrEmpty();
        if (!parts || disabled)
            await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        else
            await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalRead_ComposesIndependentReadsInOrder(bool complete)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = CompositionTool("query_habits");
        var goals = CompositionTool("query_goals");
        var handler = CreateHandler(habits, goals);
        UseProductionReadExecutor(habits, goals);
        SetupAiResponse(new AiResponse
        {
            TextMessage = "Here are your habits and goals.",
            ToolCalls = [new(habits.Name, "habits", ParseArguments("{\"orbit_read_complete\":true}")),
                new(goals.Name, "goals", ParseArguments(complete ? "{\"orbit_read_complete\":true}" : "{\"orbit_read_complete\":false}"))],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("No additional data.");

        var result = await handler.Handle(new(UserId, "Show habits and goals", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList", "goalList"], SupportsHabitListCard: true,
            SupportsGoalListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Kind).Should().Equal("habitList", "goalList");
        result.Value.Parts![0].Should().BeOfType<ChatBlockPart>();
        await _aiIntentService.Received(complete ? 0 : 1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalRead_CompoundReadRequiresEveryBlock(bool failed)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = CompositionTool("query_habits");
        var goals = ReadTool("query_goals", failed ? new ToolResult(false, Error: "Read failed") : new ToolResult(true));
        var handler = CreateHandler(habits, goals);
        UseProductionReadExecutor(habits, goals);
        SetupAiResponse(new AiResponse
        {
            TextMessage = "Here are your habits and goals.",
            ToolCalls = [new(habits.Name, "habits", ParseArguments("{\"orbit_read_complete\":true}")), new(goals.Name, "goals", ParseArguments("{\"orbit_read_complete\":true}"))],
            ConversationContext = TestConversationContext
        });
        ContinueWithReadDirective("Narrative cannot replace a missing read.");

        var result = await handler.Handle(new(UserId, "Show habits and goals", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitList", "goalList"], SupportsHabitListCard: true,
            SupportsGoalListCard: true)), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Should().ContainSingle().Which.Kind.Should().Be("habitList");
        result.Value.AiMessage.Should().NotContain("Here are");
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalRead_IncompletePlanOrDisabledCompositionKeepsContinuation(bool enabled)
    {
        SetupUserAndPayGate();
        if (enabled) EnableComposition();
        var ranking = await ProduceRankingAsync();
        var tool = ReadTool("get_habit_ranking", new ToolResult(true, Payload: ranking));
        SetupAiResponse(ToolResponse(tool.Name, "ranking", enabled ? "{\"orbit_read_complete\":false}" : "{\"orbit_read_complete\":true}") with
        { TextMessage = "Here is your habit ranking." });
        ContinueWithReadDirective("[[orbit:ranking]]\nThere were 99 completions.");

        var result = await CreateHandler(tool).Handle(new(UserId, "Rank my habits", ClientContext: new(
            SupportsParts: true, PartKinds: ["habitRanking"])), default);

        result.IsSuccess.Should().BeTrue();
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        if (enabled)
        {
            result.Value.AiMessage.Should().BeNullOrEmpty();
            result.Value.Parts![0].Should().BeOfType<ChatBlockPart>();
        }
    }
}
