using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Tests.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task TerminalRead_CompoundFallbackReplayUsesServerComposition(string language, bool complete)
    {
        SetupExistingParents(language);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst, FeatureFlagKeys.AstraEvalTrace]);
        var promptBuilder = new SystemPromptBuilder();
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns(call => promptBuilder.BuildStatic(call.Arg<PromptBuildRequest>()));
        _promptBuilder.BuildDynamic(Arg.Any<PromptBuildRequest>()).Returns(call => promptBuilder.BuildDynamic(call.Arg<PromptBuildRequest>()));
        var pt = language == "pt-BR";
        var habits = new[] { CreateHabit(pt ? "Beber água" : "Water"), CreateHabit(pt ? "Caminhada" : "Walk") };
        for (var index = 0; index < habits.Length; index++) habits[index].SetPosition(index);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>()).Returns(habits);
        var goal = Goal.Create(UserId, pt ? "Hidratação constante" : "Consistent hydration", 10, pt ? "sessões" : "sessions").Value;
        _goalRepo.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>()).Returns([goal]);
        var queryHabits = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var queryGoals = new QueryGoalsTool(_goalRepo, _userDateService, _goalProgressReadSyncer);
        var replay = CapturedCompositionPlan("g8-composition-compound-missing-kind-" + language + "-r1");
        if (!complete)
            replay = replay with
            {
                ToolCalls = replay.ToolCalls!.Select(call =>
                {
                    var args = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
                    args[TerminalReadDirective.CompleteArgument] = false;
                    return call with { Args = JsonSerializer.SerializeToElement(args) };
                }).ToArray()
            };
        var clock = new ChatTimingClock();
        _turnClock = clock;
        var handler = CreateHandler(queryHabits, queryGoals, new GetHabitMonthTool(_mediator), new GetPeriodAnalyticsTool(_mediator));
        UseProductionReadExecutor(queryHabits, queryGoals);
        var completion = CreateTimedCompletion(clock);
        AiToolRequest? modelRequest = null;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                modelRequest = call.Arg<AiToolRequest>();
                return completion.RunAsync(UserId, _ => Task.FromResult(Result.Success(replay)), default, "{}");
            });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => completion.RunAsync(UserId, _ => Task.FromResult(Result.Success(new AiResponse
            { TextMessage = "[[orbit:read:capture_0]]\n[[orbit:read:capture_1]]" })), default, "{}"));

        var result = await handler.Handle(new(UserId,
            pt ? "Mostre meus hábitos e minhas metas juntos." : "Show my habits and goals together.",
            EvalTraceRequested: true, ClientContext: new(SupportsParts: true, PartKinds: [], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Operations!.Select(operation => operation.OperationId).Should().Equal("query_habits", "query_goals");
        result.Value.Operations.Should().OnlyContain(operation => operation.Status == AgentOperationStatus.Succeeded && operation.ReadBlock != null);
        result.Value.Parts.Should().HaveCount(2).And.AllBeOfType<ChatTextPart>();
        result.Value.AiMessage.Should().Be(pt
            ? "Hábitos: 2\nBeber água (Hoje)\nCaminhada (Hoje)\n\nMetas: 1\nHidratação constante: 0/10 sessões"
            : "Habits: 2\nWater (Today)\nWalk (Today)\n\nGoals: 1\nConsistent hydration: 0/10 sessions");
        result.Value.EvalTrace!.Timing!.Phases.Count(phase => phase.Name == "model_call").Should().Be(complete ? 1 : 2);
        await _aiIntentService.Received(complete ? 0 : 1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        modelRequest.Should().NotBeNull();
        modelRequest!.SystemPrompt.Should()
            .NotContain("For unsupported clients, use only its returned habit facts in text.")
            .And.NotContain("For an unsupported metric, answer from the queried facts in text.")
            .And.NotContain("For an unsupported subtype, answer from the queried facts in text.")
            .And.Contain("The server renders unsupported read blocks and subtypes as readable text from the same typed results.")
            .And.Contain("For a compound habits-and-goals read, call query_habits and query_goals together and set orbit_read_complete=true on both when they fully answer the request.");
        var declarations = JsonSerializer.SerializeToElement(modelRequest.ToolDeclarations);
        foreach (var name in new[] { "query_habits", "query_goals" })
        {
            var schema = declarations.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == name)
                .GetProperty("parameters");
            schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Contain(TerminalReadDirective.CompleteArgument);
            schema.GetProperty("properties").GetProperty(TerminalReadDirective.CompleteArgument).GetProperty("description").GetString()
                .Should().Contain("including compound reads and readable fallback text");
        }
    }
}
